---
name: pr-review-loop
description: >
  This skill should be used when the user asks to "open a PR", "submit a PR",
  "get a review", "address review comments", "drive the PR through review",
  or says the code work is done and ready for review in the Dev Proxy
  repository. Opens a pull request to dotnet/dev-proxy main with the gh CLI,
  requests a GitHub Copilot code review, and repeats fix-and-re-review rounds
  until a stop condition is met. Never merges.
---

# PR Review Loop

Open a pull request, get a Copilot code review, address the feedback, and repeat until the review comes back clean or a stop condition is hit.

## Guardrails

These stay with a human. Never:

- Merge, close, or approve the pull request, or enable auto-merge
- Change repository settings, rulesets, branch protection, secrets, or release and deployment workflows
- Force-push over someone else's commits or rewrite the history of the base branch
- Dismiss reviews, or resolve threads you didn't address

## Stop Conditions

Stop the loop and report to the user when any of these is true:

1. **Clean review:** the latest Copilot review of the current head commit has no new inline comments and its body reports no unaddressed findings, CI is green, and every earlier thread is resolved or has your reply
2. **Round limit:** 20 review rounds have been completed
3. **Repeated CI failure:** the same CI check fails 3 times in a row with the same error
4. **Stuck:** a fix needs a decision from a human (design choice, behavior change, scope change). Ask; don't guess
5. **Timeout:** Copilot hasn't submitted a review, or CI hasn't finished, 30 minutes after the review was requested
6. **Nothing left to review:** a round produced only advisory comments that you declined, so no code changed

## Prerequisites

- `gh` CLI 2.87 or newer (adds `gh pr edit --add-reviewer @copilot`), signed in (`gh auth status`) with access to the fork and to `dotnet/dev-proxy`
- Remotes: `upstream` points to `dotnet/dev-proxy`, `origin` points to your fork
- Work is committed on a feature branch (not `main`)

Set these once and reuse them in the commands below:

```bash
UPSTREAM=dotnet/dev-proxy
FORK_OWNER=$(gh api user --jq .login)
BRANCH=$(git branch --show-current)
```

## Process

### Step 1: Verify Locally

Build and run the tests. Fix any failures before you continue.

```bash
dotnet build DevProxy.sln
dotnet test DevProxy.sln
```

Skip this step for documentation-only changes.

### Step 2: Open the Pull Request

If a pull request for the branch is already open, reuse it and skip to Step 5 to address its open feedback:

```bash
PR=$(gh pr view "$FORK_OWNER:$BRANCH" --repo "$UPSTREAM" --json number,state \
  --jq 'select(.state=="OPEN") | .number' 2>/dev/null)
```

Otherwise, rebase on the latest upstream `main` and push the branch to the fork. Only rebase a branch that hasn't been pushed yet. Once it's published, don't rewrite its history; if it needs upstream changes, merge `upstream/main` instead.

```bash
git fetch upstream main
git rebase upstream/main
git push -u origin "$BRANCH"
```

Open the pull request against upstream `main`. Describe what changed and why, and reference the issue it closes (`Closes #123`):

```bash
gh pr create --repo "$UPSTREAM" --base main --head "$FORK_OWNER:$BRANCH" \
  --title "<title>" --body-file <path-to-description.md>
PR=$(gh pr view "$FORK_OWNER:$BRANCH" --repo "$UPSTREAM" --json number --jq .number)
```

### Step 3: Request a Copilot Review

Record the request time first, so that an earlier review of the same commit isn't mistaken for the new one:

```bash
REQUESTED_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)
gh pr edit "$PR" --repo "$UPSTREAM" --add-reviewer "@copilot"
```

Run the same commands to re-request a review in later rounds. Each request counts as one round.

### Step 4: Wait for CI and the Review

Poll CI and the review together every 60 seconds, for up to 30 minutes after the request (stop condition 5). The loop ends when CI has finished and Copilot submitted a review of the current head commit after `REQUESTED_AT`:

```bash
HEAD_SHA=$(git rev-parse HEAD)
REVIEW=""
for i in $(seq 1 30); do
  REVIEW=$(gh api graphql -F owner=dotnet -F name=dev-proxy -F pr="$PR" -f query='
  query($owner:String!,$name:String!,$pr:Int!){
    repository(owner:$owner,name:$name){
      pullRequest(number:$pr){
        reviews(last:50){
          nodes{ author{login} submittedAt commit{oid} comments{totalCount} body }
        }
      }
    }
  }' --jq "[.data.repository.pullRequest.reviews.nodes[]
    | select(.author.login==\"copilot-pull-request-reviewer\" and .commit.oid==\"$HEAD_SHA\" and .submittedAt > \"$REQUESTED_AT\")]
    | last // empty")
  PENDING=$(gh pr checks "$PR" --repo "$UPSTREAM" --json bucket --jq '[.[] | select(.bucket=="pending")] | length')
  [ -n "$REVIEW" ] && [ "$PENDING" = "0" ] && break
  sleep 60
done
echo "$REVIEW"
gh pr checks "$PR" --repo "$UPSTREAM"
```

If `REVIEW` is empty or checks are still pending, stop (stop condition 5).

If CI failed, read the logs (`gh run view <run-id> --repo "$UPSTREAM" --log-failed`) and fix the cause together with the review comments in Step 5. Track how many times in a row each check failed with the same error (stop condition 3).

`comments.totalCount` is the number of new inline comments in that round. Also read the review `body`: Copilot lists some findings, such as issues it missed in code that didn't change since the last review, only in the body and not as inline comments. Treat those like inline comments.

If the review has no new inline comments, its body reports no findings you haven't addressed, and CI is green, stop (stop condition 1).

### Step 5: Address Each Comment

List unresolved review threads. `--paginate` fetches all pages, so threads from earlier rounds don't hide new ones:

```bash
gh api graphql --paginate -F owner=dotnet -F name=dev-proxy -F pr="$PR" -f query='
query($owner:String!,$name:String!,$pr:Int!,$endCursor:String){
  repository(owner:$owner,name:$name){
    pullRequest(number:$pr){
      reviewThreads(first:100,after:$endCursor){
        pageInfo{ hasNextPage endCursor }
        nodes{
          id isResolved path line
          comments(first:20){ nodes{ databaseId author{login} body } }
        }
      }
    }
  }
}' --jq '.data.repository.pullRequest.reviewThreads.nodes[] | select(.isResolved|not)'
```

Verify each claim against the code before acting on it. Reviewers can be wrong. Then classify it:

| Type | Examples | Action |
|------|----------|--------|
| **Blocking** | Incorrect behavior, security issue, data loss, crash, missing test for new or fixed behavior | Fix it |
| **Advisory** | Naming, style, wording, optional refactoring, speculative concerns | Fix it if it's an improvement, otherwise reply explaining why not |

If a fix needs a decision from a human, stop (stop condition 4) and ask.

After you push the fixes (Step 6), reply to every thread. Use the `databaseId` of the first comment in the thread:

```bash
gh api -X POST "repos/$UPSTREAM/pulls/$PR/comments/<databaseId>/replies" \
  -f body="Fixed in <sha>: <one line summary>"
```

Resolve the threads you fixed:

```bash
gh api graphql -f id=<thread-id> -f query='
mutation($id:ID!){ resolveReviewThread(input:{threadId:$id}){ thread{ isResolved } } }'
```

When you decline a suggestion, reply with the reason and leave the thread unresolved, so that a maintainer can decide.

Findings that appear only in the review body have no thread. Address them in one PR comment:

```bash
gh pr comment "$PR" --repo "$UPSTREAM" --body "<finding>: fixed in <sha> / not changed because <reason>"
```

### Step 6: Push and Repeat

1. For code changes, run Step 1 again
2. Commit with a message that describes the fixes, and push: `git push origin "$BRANCH"`
3. Reply to and resolve the threads (Step 5)
4. If nothing changed in this round, stop (stop condition 6)
5. Otherwise go back to Step 3

### Step 7: Report

When the loop stops, report to the user:

- Pull request URL
- Number of rounds and which stop condition ended the loop
- Comments fixed, comments declined with reasons, and anything that needs a human decision
- Final CI status

The pull request stays open for a maintainer to review and merge.
