# What was actually wrong with the data

Not what we expected — and that is the interesting part.

The original design anticipated the classic AIS sentinel values: speed of 102.3 kn, heading 511,
latitude 91 paired with longitude 181. Every one of those assumptions was checked against a real
file before a parser was written. Most were wrong:

| Expected | Measured on 1,717,280 real rows |
|---|---|
| SOG sentinel `102.3` | **Zero.** DMA blanks unavailable values instead — 146,612 blank SOG (8.5%) |
| Heading sentinel `511` | **Zero.** 438,778 blank (26%) |
| Latitude 91 / longitude **181** | Latitude `91.000000` / longitude **`0.000000`**, 7,013 rows. A `lon == 181` test catches nothing |
| Impossible speeds > 40 kn | **5 rows.** Maximum observed: 70.8 kn |
| Timestamps out of order per vessel | **Structurally impossible** — the file is globally time-sorted |
| Invalid MMSIs (not 9 digits) | 112,774 rows — all valid 7-digit base stations and 4-digit AtoN, not corruption |

The coordinates, speeds and identifiers in this feed are *clean*. What is actually wrong with it is
three things nobody warns you about:

**38.2% of the feed is duplicate.** 654,880 of 1,717,280 rows are exact duplicates on
`(mmsi, timestamp, latitude, longitude)` *within a single file*, because the DMA merges several
receiving stations. Deduplication is not a tidiness measure here; it is most of the work.

**63.1% of stationary tankers claim to be moving.** Of 11,710 tanker fixes below 0.5 kn, 7,387 report
navigational status `Under way using engine`. The vessel's own transponder contradicts the vessel's
own speed roughly two times in three. This project records the disagreement rather than picking a
winner.

**0.25% of rows break a naive comma split.** 4,261 rows carry quoted vessel names containing commas.
Split on `,` and every subsequent column shifts — speed read from the course field, ship type from
the name field. Parsed with a conformant CSV reader, exactly **1** row is malformed.
