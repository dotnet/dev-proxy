#!/usr/bin/env python3
"""Checks for Dev Proxy JSON schemas.

Subcommands:
  shipped   Fail if a change touches schemas/vX.Y.Z/ of an already released version.
  validate  Check that all schemas are valid JSON Schema and that JSON/YAML files in
            the repo declaring a Dev Proxy $schema validate against the local copy.
"""

import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SCHEMAS_DIR = REPO_ROOT / "schemas"
VERSION_PROJECT = REPO_ROOT / "DevProxy" / "DevProxy.csproj"
BYPASS_LABEL = "schema-fix"
SCHEMA_URL_RE = re.compile(
    r"^https://raw\.githubusercontent\.com/dotnet/dev-proxy/[^/]+/schemas/(?P<path>v[^/]+/[^/]+\.json)$"
)
# Files intentionally not validated against their schema.
SKIP_VALIDATION = {
    # Scaffold for `devproxy config new`: empty `-` list items are placeholders
    # for the user to fill in and resolve to null, which the schema rejects.
    "DevProxy/config-templates/devproxyrc.yaml",
}
YAML_MODELINE_RE = re.compile(r"^(?P<indent>\s*)#\s*yaml-language-server:\s*\$schema=(?P<url>\S+)")
VERSION_RE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")
IN_ACTIONS = os.environ.get("GITHUB_ACTIONS") == "true"


def git(*args: str) -> str:
    return subprocess.run(
        ["git", *args], cwd=REPO_ROOT, check=True, capture_output=True, text=True
    ).stdout


def parse_version(value: str):
    match = VERSION_RE.match(value)
    return tuple(int(part) for part in match.groups()) if match else None


def current_version() -> str:
    version = ET.parse(VERSION_PROJECT).getroot().findtext("./PropertyGroup/Version")
    if not version:
        sys.exit(f"Could not read <Version> from {VERSION_PROJECT.relative_to(REPO_ROOT)}")
    return f"v{version.strip()}"


def released_tags(remote: str) -> set[str]:
    output = git("ls-remote", "--tags", "--refs", remote, "refs/tags/v*")
    return {line.split("refs/tags/", 1)[1] for line in output.splitlines() if "refs/tags/" in line}


def cmd_shipped(args: argparse.Namespace) -> int:
    changed = [
        line
        for line in git("diff", "--name-only", "--no-renames", args.base, args.head, "--", "schemas/").splitlines()
        if line
    ]
    tags = released_tags(args.remote)
    current = current_version()
    current_parsed = parse_version(current)

    def is_shipped(version: str) -> bool:
        if version in tags:
            return True
        parsed = parse_version(version)
        # Versions older than the version in development are released, even if
        # the tag isn't available (e.g. in forks without tags).
        return bool(parsed and current_parsed and parsed < current_parsed)

    violations: dict[str, list[str]] = {}
    for path in changed:
        parts = path.split("/")
        if len(parts) >= 3 and is_shipped(parts[1]):
            violations.setdefault(parts[1], []).append(path)

    print(f"Version in development: {current}")
    print(f"Changed schema files: {len(changed)}")
    if not violations:
        print("OK: no schemas of released versions were changed.")
        return 0

    lines = ["This change modifies schemas of already released Dev Proxy versions:"]
    for version in sorted(violations, key=lambda v: parse_version(v) or (0, 0, 0)):
        lines.append(f"  {version}:")
        lines.extend(f"    - {path}" for path in violations[version])
    lines += [
        "",
        "Released schemas are referenced by users' config files and must not change.",
        f"Make schema changes in schemas/{current}/ instead.",
        "",
        "If this is an intentional fix of a historical schema (e.g. the schema doesn't",
        "match what that version of Dev Proxy actually supports), ask a maintainer to add",
        f"the '{BYPASS_LABEL}' label to the pull request. The check re-runs automatically",
        "when labels change.",
    ]
    message = "\n".join(lines)
    print(message)

    if args.bypass:
        print(f"\nBypassed: the pull request has the '{BYPASS_LABEL}' label.")
        if IN_ACTIONS:
            print(f"::warning title=Released schemas changed::Bypassed with the '{BYPASS_LABEL}' label: {', '.join(sorted(violations))}")
        return 0

    if IN_ACTIONS:
        print("::error title=Released schemas changed::" + message.replace("%", "%25").replace("\n", "%0A"))
    return 1


def load_json(path: Path):
    with path.open(encoding="utf-8-sig") as file:
        return json.load(file)


def find_schema_refs(node, pointer=""):
    if isinstance(node, dict):
        value = node.get("$schema")
        if isinstance(value, str):
            yield pointer, value, node
        for key, child in node.items():
            yield from find_schema_refs(child, f"{pointer}/{key}")
    elif isinstance(node, list):
        for index, child in enumerate(node):
            yield from find_schema_refs(child, f"{pointer}/{index}")


def find_yaml_schema_refs(text: str):
    """Yields (pointer, url, instance) for `# yaml-language-server: $schema=` modelines.

    A modeline without indentation applies to the whole document. An indented
    modeline applies to the mapping that starts on the next line at the same
    indentation (Dev Proxy's convention for plugin config sections).
    """
    import yaml

    document = yaml.safe_load(text)
    lines = text.splitlines()
    mappings: dict[tuple[int, int], list] = {}

    def walk(node, path):
        if isinstance(node, yaml.MappingNode):
            mappings[(node.start_mark.line, node.start_mark.column)] = path
            for key, value in node.value:
                walk(value, [*path, key.value])
        elif isinstance(node, yaml.SequenceNode):
            for index, value in enumerate(node.value):
                walk(value, [*path, index])

    walk(yaml.compose(text), [])

    for number, line in enumerate(lines):
        match = YAML_MODELINE_RE.match(line)
        if not match:
            continue
        indent = len(match.group("indent"))
        if indent == 0:
            yield "", match.group("url"), document
            continue
        next_line = next(
            (n for n in range(number + 1, len(lines)) if lines[n].strip() and not lines[n].lstrip().startswith("#")),
            None,
        )
        path = mappings.get((next_line, indent))
        if path is None:
            yield f"(line {number + 1})", match.group("url"), None
            continue
        instance = document
        for part in path:
            instance = instance[part]
        yield "".join(f"/{part}" for part in path), match.group("url"), instance


def cmd_validate(_: argparse.Namespace) -> int:
    from jsonschema import FormatChecker
    from jsonschema.exceptions import SchemaError
    from jsonschema.validators import validator_for

    errors: list[str] = []
    schemas: dict[str, object] = {}

    schema_files = sorted(SCHEMAS_DIR.glob("*/*.json"))
    for path in schema_files:
        rel = path.relative_to(REPO_ROOT).as_posix()
        try:
            schema = load_json(path)
            validator_for(schema).check_schema(schema)
            schemas[path.relative_to(SCHEMAS_DIR).as_posix()] = schema
        except json.JSONDecodeError as ex:
            errors.append(f"{rel}: invalid JSON: {ex}")
        except SchemaError as ex:
            errors.append(f"{rel}: invalid JSON Schema at /{'/'.join(map(str, ex.path))}: {ex.message}")

    import yaml

    files = [
        REPO_ROOT / f
        for f in git("ls-files", "-z", "*.json", "*.yaml", "*.yml").split("\0")
        if f and not f.startswith("schemas/") and f not in SKIP_VALIDATION
    ]
    checked = 0
    for path in files:
        rel = path.relative_to(REPO_ROOT).as_posix()
        text = path.read_text(encoding="utf-8-sig", errors="replace")
        # Only files declaring a Dev Proxy schema are in scope.
        if "raw.githubusercontent.com/dotnet/dev-proxy/" not in text:
            continue
        try:
            if path.suffix == ".json":
                refs = list(find_schema_refs(json.loads(text)))
            else:
                refs = list(find_yaml_schema_refs(text))
        except (json.JSONDecodeError, yaml.YAMLError) as ex:
            errors.append(f"{rel}: invalid {path.suffix.lstrip('.').upper()}: {ex}")
            continue
        for pointer, url, instance in refs:
            match = SCHEMA_URL_RE.match(url)
            if not match:
                continue
            schema_path = match.group("path")
            schema = schemas.get(schema_path)
            if schema is None:
                errors.append(f"{rel}#{pointer}: $schema {url} doesn't map to a valid local schema (schemas/{schema_path})")
                continue
            if instance is None and path.suffix != ".json":
                errors.append(f"{rel} {pointer}: $schema modeline isn't followed by a mapping at the same indentation")
                continue
            checked += 1
            validator = validator_for(schema)(schema, format_checker=FormatChecker())
            for error in sorted(validator.iter_errors(instance), key=lambda e: list(map(str, e.absolute_path))):
                location = "".join(f"/{part}" for part in error.absolute_path)
                errors.append(f"{rel}#{pointer}{location}: {error.message} (schemas/{schema_path})")

    print(f"Checked {len(schema_files)} schemas and {checked} JSON/YAML documents referencing them.")
    if errors:
        for error in errors:
            print(f"::error::{error}" if IN_ACTIONS else error, file=sys.stderr)
        return 1
    print("OK")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    shipped = sub.add_parser("shipped", help="check that released schemas aren't modified")
    shipped.add_argument("--base", required=True, help="base commit to compare against")
    shipped.add_argument("--head", default="HEAD", help="head commit (default: HEAD)")
    shipped.add_argument("--remote", default="origin", help="git remote to read release tags from (default: origin)")
    shipped.add_argument("--bypass", action="store_true", help=f"report but don't fail (set when the PR has the '{BYPASS_LABEL}' label)")
    shipped.set_defaults(func=cmd_shipped)

    validate = sub.add_parser("validate", help="validate schemas and JSON/YAML files referencing them")
    validate.set_defaults(func=cmd_validate)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
