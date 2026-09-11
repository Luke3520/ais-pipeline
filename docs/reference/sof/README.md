# Statement of Facts reference

Source material for M7. Not code, not decisions — the documents the comparison is modelled on.

## `sample-BIMCO-...-oil-and-chemical-tank-vessels-short-form.pdf`

BIMCO's **Standard Statement of Facts (Oil and Chemical Tank Vessels), Short Form** — the tanker
variant, recommended by BIMCO and FONASBA. This is the blank form, watermarked "Sample Copy", as
published at
<https://www.bimco.org/contractual-affairs/bimco-contracts/contracts/standard-statement-of-facts/>.

**Not committed.** It is BIMCO's copyright, distributed as a sample from their own site; this repo
is public, and redistributing someone else's form is not ours to do. It is gitignored, and the URL
above is the citation. Anyone reproducing this work can fetch it themselves.

## What it gave us

The **event vocabulary**, exactly — forty numbered boxes, no guessing required. See
`docs/adr/` for how they map onto what AIS can and cannot corroborate.

## What it did not give us

**Real timings.** It is a blank form, so the discrepancy tolerance remains uncalibrated: the gap
between box 5 "Vessel moored" and the moment AIS sees a vessel stop moving is a real, physical
difference of minutes, and nothing here measures it.

## What it told us by accident

It is a **scan**. `pdfinfo` reports Creator `Canon iR-ADV C5255` — an office photocopier — and the
file carries two characters of text layer and page images instead. That is what a real Statement of
Facts arrives as, and it is the strongest possible argument for keeping document extraction fenced
away from the comparison logic: the input to that stage is OCR output, not text.
