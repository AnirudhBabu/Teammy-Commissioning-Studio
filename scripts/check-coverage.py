#!/usr/bin/env python3
"""Merge per-test Cobertura reports by source line and gate the shared rules."""
from pathlib import Path
import argparse
import sys
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('directory', type=Path)
parser.add_argument('--minimum', type=float, default=80.0)
args = parser.parse_args()
lines = {}
reports = list(args.directory.rglob('coverage.cobertura.xml'))
for report in reports:
    for package in ET.parse(report).getroot().findall('./packages/package'):
        name = package.attrib['name']
        if name not in {'Teammy.Domain', 'Teammy.Application'}:
            continue
        for cls in package.findall('./classes/class'):
            filename = cls.attrib['filename'].replace('\\', '/')
            for line in cls.findall('./lines/line'):
                key = (name, filename, int(line.attrib['number']))
                lines[key] = lines.get(key, False) or int(line.attrib['hits']) > 0
if not lines:
    sys.exit('FAIL: no Domain/Application line coverage found; run tests with XPlat Code Coverage.')
covered = sum(lines.values())
percentage = 100 * covered / len(lines)
print(f'Shared rules line coverage: {percentage:.2f}% ({covered}/{len(lines)}); minimum {args.minimum:.0f}%')
if percentage < args.minimum:
    sys.exit(1)
