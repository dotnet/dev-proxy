#!/usr/bin/env python3
"""Checks for Dev Proxy JSON schemas.

Subcommands:
  shipped   Fail if a change touches schemas/vX.Y.Z/ of an already released version.
  validate  Check that all schemas are valid JSON Schema and that JSON/YAML files in
            the repo declaring a Dev Proxy $schema validate against the local copy.
"""

import argparse
import json
import math
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
    r"^https://raw\.githubusercontent\.com/dotnet/dev-proxy/.+/schemas/(?P<path>.+)$"
)
# Files intentionally not validated against their schema.
SKIP_VALIDATION = {
    # Scaffold for `devproxy config new`: empty `-` list items are placeholders
    # for the user to fill in and resolve to null, which the schema rejects.
    "DevProxy/config-templates/devproxyrc.yaml",
}
YAML_MODELINE_RE = re.compile(r"^(?P<indent>\s*)#\s*yaml-language-server:\s*\$schema=(?P<url>\S+)")
VERSION_RE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")
# Text that may contain a Dev Proxy schema URL, also with JSON-escaped slashes.
DEV_PROXY_REF_RE = re.compile(r"dotnet\\?/dev-proxy")
YAML_INT_RE = re.compile(r"^\s*[+-]?\d+\s*$", re.ASCII)
YAML_FLOAT_RE = re.compile(r"^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*$|^\s*[+-]?(infinity|nan)\s*$", re.IGNORECASE | re.ASCII)
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


def reject_constant(value: str):
    raise json.JSONDecodeError(f"{value} isn't valid JSON", value, 0)


def parse_json(text: str):
    return json.loads(text, parse_constant=reject_constant)


def load_json(path: Path):
    return parse_json(path.read_text(encoding="utf-8-sig"))


def convert_yaml_node(node):
    """Converts a composed YAML node the same way as Dev Proxy (ProxyYaml.ConvertNode)."""
    import yaml

    if isinstance(node, yaml.MappingNode):
        merged, explicit = {}, {}
        for key, value in node.value:
            if not isinstance(key, yaml.ScalarNode):
                continue
            if key.value == "<<":
                sources = value.value if isinstance(value, yaml.SequenceNode) else [value]
                for source in sources:
                    if isinstance(source, yaml.MappingNode):
                        merged.update(convert_yaml_node(source))
            else:
                explicit[key.value] = convert_yaml_node(value)
        return {**merged, **explicit}
    if isinstance(node, yaml.SequenceNode):
        return [convert_yaml_node(item) for item in node.value]
    if isinstance(node, yaml.ScalarNode):
        value = node.value
        # Only plain (unquoted) scalars get type inference.
        if node.style is not None:
            return value
        if value in ("", "~") or value.lower() == "null":
            return None
        if value.lower() in ("true", "yes", "on"):
            return True
        if value.lower() in ("false", "no", "off"):
            return False
        if YAML_INT_RE.match(value) and -(2**63) <= int(value) < 2**63:
            return int(value)
        if YAML_FLOAT_RE.match(value):
            number = float(value)
            # System.Text.Json can't serialize these, so Dev Proxy fails to load the file.
            if not math.isfinite(number):
                raise yaml.YAMLError(f"non-finite number '{value}' at line {node.start_mark.line + 1}")
            return number
        return value
    return None


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
    """Yields (pointer, url, instance) for `$schema` keys and
    `# yaml-language-server: $schema=` modelines.

    A modeline without indentation applies to the whole document. An indented
    modeline applies to the mapping that starts on the next line at the same
    indentation (Dev Proxy's convention for plugin config sections).
    """
    import yaml

    # Load the whole stream like Dev Proxy does, so errors in later documents are reported.
    documents = list(yaml.compose_all(text))
    root = documents[0] if documents else None
    document = convert_yaml_node(root) if root is not None else None
    lines = text.splitlines()
    mappings: dict[tuple[int, int], list] = {}

    def walk(node, path):
        if isinstance(node, yaml.MappingNode):
            mappings[(node.start_mark.line, node.start_mark.column)] = path
            for key, value in node.value:
                if isinstance(key, yaml.ScalarNode) and key.value != "<<":
                    walk(value, [*path, key.value])
        elif isinstance(node, yaml.SequenceNode):
            for index, value in enumerate(node.value):
                walk(value, [*path, index])

    walk(root, [])

    seen = set()
    for pointer, url, instance in find_schema_refs(document):
        seen.add((pointer, url))
        yield pointer, url, instance

    for number, line in enumerate(lines):
        match = YAML_MODELINE_RE.match(line)
        if not match:
            continue
        indent = len(match.group("indent"))
        if indent == 0:
            if ("", match.group("url")) not in seen:
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
        pointer = "".join(f"/{part}" for part in path)
        if (pointer, match.group("url")) not in seen:
            yield pointer, match.group("url"), instance


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
            validator = validator_for(schema, default=None)
            if validator is None:
                errors.append(f"{rel}: missing or unrecognized $schema draft: {schema.get('$schema') if isinstance(schema, dict) else None!r}")
                continue
            validator.check_schema(schema)
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
        try:
            if path.suffix == ".json":
                refs = list(find_schema_refs(parse_json(text)))
            else:
                refs = list(find_yaml_schema_refs(text))
        except (json.JSONDecodeError, yaml.YAMLError) as ex:
            # Only files declaring a Dev Proxy schema are in scope.
            if DEV_PROXY_REF_RE.search(text):
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
