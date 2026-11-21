"""Seed the station matrix from a full probe or append a targeted recheck."""

import argparse
import csv
from datetime import date
import json
from pathlib import Path


STAGES = ("home", "category", "search", "detail", "play")
COLUMNS = ("site_key", "name", "type", "api", *STAGES, "fidelity_notes", "updated_at")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("probe", type=Path)
    parser.add_argument("--matrix", type=Path, default=Path("docs/site-matrix.csv"))
    parser.add_argument("--date", type=date.fromisoformat, required=True)
    parser.add_argument("--note", default="")
    args = parser.parse_args()

    report = args.probe.resolve().relative_to(Path.cwd().resolve()).as_posix()
    rows = {}
    if args.matrix.exists():
        with args.matrix.open(encoding="utf-8-sig", newline="") as source:
            rows.update((row["site_key"], row) for row in csv.DictReader(source))
        backup = args.probe.with_name(f"{args.matrix.name}.before.bak")
        if not backup.exists():
            with backup.open("xb") as target:
                target.write(args.matrix.read_bytes())

    records = [json.loads(line) for line in args.probe.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    for record in sorted(records, key=lambda row: (row["Type"] == "live", row["Key"])):
        key = record["Key"]
        notes = [record["Implementation"]]
        notes += [f"{stage}：{record[stage.title()]['Summary']}" for stage in STAGES]
        notes += [f"证据 {report}"]
        if args.note:
            notes.append(args.note)
        previous = rows.get(key, {})
        statuses = {stage: record[stage.title()]["Status"] for stage in STAGES}
        if any(previous.get(stage) == "PASS" for stage in STAGES):
            for stage in STAGES:
                if previous.get(stage) == "PASS" and statuses[stage] != "FAIL":
                    statuses[stage] = "PASS"
            notes.append(f"已有原版对照与实机证据：{previous['fidelity_notes']}")
        rows[key] = dict(zip(COLUMNS[:4], (key, record["Name"], record["Type"], record["Api"])),
                        **statuses, fidelity_notes=" ｜ ".join(notes), updated_at=args.date.isoformat())

    temporary = args.matrix.with_suffix(".csv.tmp")
    with temporary.open("w", encoding="utf-8-sig", newline="") as target:
        writer = csv.DictWriter(target, fieldnames=COLUMNS)
        writer.writeheader()
        writer.writerows(rows.values())
    temporary.replace(args.matrix)


if __name__ == "__main__":
    main()
