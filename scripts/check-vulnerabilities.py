#!/usr/bin/env python3
"""Make the machine-readable NuGet audit fail on advisories or source failures."""
import json
from pathlib import Path
import sys

data = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
failures = []
for problem in data.get('problems', []):
    failures.append(str(problem))
for project in data.get('projects', []):
    for framework in project.get('frameworks', []):
        for group in ('topLevelPackages', 'transitivePackages'):
            for package in framework.get(group, []):
                for advisory in package.get('vulnerabilities', []):
                    failures.append(f"{package['id']} {package.get('resolvedVersion', '')}: "
                                    f"{advisory.get('severity', 'unknown')} {advisory.get('advisoryurl', '')}")
if failures:
    print('\n'.join(failures), file=sys.stderr)
    sys.exit(1)
print('NuGet audit: no known vulnerable direct or transitive packages in this result.')
