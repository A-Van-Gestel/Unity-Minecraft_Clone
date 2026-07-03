# Cave Density Analyzer — Report Sections & Metric Interpretation

Companion reference for the `cave-tuning` skill. Covers what `CaveDensityAnalyzer.RunAnalysis`
reports and how to read the numbers.

## What it reports

- **Overview**: total cave air, overall/median/min/max density, chunks with no caves
- **Density Distribution**: bucket counts (0%, <2%, 2-5%, 5-10%, >10%)
- **Pocket Analysis**: per-chunk pocket counts, sizes (smallest/median), large pocket distribution
- **Cross-Chunk Networks**: global network stats after merging pockets across chunk boundaries via union-find (network count, sizes, chunks spanned, merge amplification, global connectivity)
- **Network Y-Range**: true vertical extent of merged networks (min/median/avg/max Y-span, largest network's Y-range)
- **Network Isolation**: nearest-neighbor centroid distance between networks in chunk units (min/median/avg)
- **Shape Quality**: tip/thin/open block ratios with quality assessment
- **Y-Level Histogram**: vertical density profile showing cave air count per Y-level — use this to verify surface penetration (check for nonzero carve counts at/above the reported avg surface height)
- **Heatmap**: spatial density grid (X/Z) for visualizing cave zone clustering
- **Layer Breakdown**: per-layer block counts and chunk coverage — essential for spotting when one layer dominates

## Interpreting results

| Metric                | Good range | Problem indicator                                          |
|-----------------------|------------|------------------------------------------------------------|
| Overall density       | 1-6%       | >10% = too hollow, 0% = caves suppressed                   |
| Chunks with no caves  | 15-40%     | 0% = no zone variation, >60% = too sparse                  |
| Tip blocks            | <15%       | >30% = heavy artifacting                                   |
| Open blocks           | >25%       | <10% = all narrow tunnels                                  |
| Global connectivity   | 0.1-0.5    | >0.7 = one dominant cave system, <0.05 = highly fragmented |
| Merge amplification   | 2-10x      | >20x = per-chunk stats massively understate network scale  |
| Max chunks spanned    | 3-15       | >30 = cave system spans most of the grid                   |
| Network median Y-span | 15-40      | >60 = full-depth systems, <5 = flat horizontal layers      |
| Network isolation     | 2-4 chunks | >6 = very sparse, <1 = networks feel continuous            |
| Median Y-span         | 5-15       | >25 = mostly vertical shafts, <=3 = flat pancake caves     |

## Grid size vs zone frequency

The analysis grid must span enough zone noise wavelengths to observe variation:

| Zone freq | 256-block grid (8 chunks) | Wavelengths                          |
|-----------|---------------------------|--------------------------------------|
| 0.003     | ~0.77                     | Too few — can't see clustering       |
| 0.006     | ~1.5                      | Minimal variation visible            |
| 0.008     | ~2.0                      | Good — multiple cave/no-cave regions |
| 0.010     | ~2.6                      | Good                                 |
| 0.040     | ~10.2                     | Many small clusters                  |

Rule: use grid size 8+ with zone frequencies 0.006+, or increase grid size for lower frequencies.
