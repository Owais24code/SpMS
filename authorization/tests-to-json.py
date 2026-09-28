#!/usr/bin/env python3
"""Writes authorization/model.fga.tests.json from model.fga.yaml.

The backend test suite runs these same assertions through the in-process
decider (Authorization:Mode = Local), so the two deciders cannot drift apart.
Run after editing model.fga.yaml; `--check` fails when the JSON is stale (CI).
"""
import json, pathlib, sys, yaml

here = pathlib.Path(__file__).resolve().parent
src = yaml.safe_load((here / "model.fga.yaml").read_text())
out = json.dumps({"tuples": src.get("tuples", []), "tests": src.get("tests", [])}, indent=1, sort_keys=True, default=str) + "\n"
target = here / "model.fga.tests.json"
if "--check" in sys.argv:
    if not target.exists() or target.read_text() != out:
        sys.exit("authorization/model.fga.tests.json is stale: run authorization/tests-to-json.py")
else:
    target.write_text(out)
    print(f"wrote {target.name}")
