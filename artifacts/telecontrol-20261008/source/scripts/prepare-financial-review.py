#!/usr/bin/env python3
"""Prepare an idempotent monthly review request; never certify tariffs automatically."""
import argparse
import datetime as dt
import hashlib
import json
import os
import tempfile
from pathlib import Path
from zoneinfo import ZoneInfo


def prepare(config_path, state_directory, today):
    config = json.loads(Path(config_path).read_text(encoding="utf-8"))
    output = Path(state_directory)
    output.mkdir(parents=True, exist_ok=True)
    month = today.replace(day=1).isoformat()
    requests = []
    for site in config.get("SiteFinance", {}).get("Sites", []):
        identity = site["SiteId"]
        if not identity or any(not (c.isascii() and (c.isalnum() or c in "-_")) for c in identity):
            raise ValueError("Invalid site identity")
        # A change in reviewed financial settings gets a new request even in
        # the same month. An unchanged pending request is not duplicated.
        parameters = {key: site.get(key) for key in (
            "Revision", "MarketBidArea", "Currency", "PriceCurrency", "ExchangeRate",
            "DistributionOperator", "VoltageClass", "Parameters")}
        digest = hashlib.sha256(json.dumps(parameters, sort_keys=True).encode()).hexdigest()
        target = output / f"{identity}-{month}-{digest[:16]}.json"
        if target.exists():
            requests.append({"site_id": identity, "request": str(target), "created": False})
            continue
        request = {
            "site_id": identity, "month": month, "parameter_digest": digest,
            "status": "requires_official_verification", "requested_on": today.isoformat(),
            "parameters": parameters,
            "steps": [
                "Open official NERC distribution schedule effective for the calculation month.",
                "Match the distribution operator and voltage class; retain effective dates, resolution, URL and rate excluding VAT.",
                "Verify transmission separately against its effective official schedule.",
                "Confirm contractual sale coefficient and currency/FX evidence separately.",
                "Reconcile supplier invoice/contract conflicts in month, voltage class, VAT and terms; retain both values.",
                "Fill Review with the exact parameter snapshot only after verification; leave Confirmed=false if unresolved.",
            ],
            "official_sources": [
                "https://www.nerc.gov.ua/sferi-diyalnosti/elektroenergiya/promislovist/tarifi-na-elektroenergiyu-dlya-nepobutovih-spozhivachiv/tarifi-na-poslugi-z-rozpodilu-elektrichnoyi-energiyi",
                "https://www.nerc.gov.ua/sferi-diyalnosti/elektroenergiya/promislovist/tarifi-na-elektroenergiyu-dlya-nepobutovih-spozhivachiv/tarif-na-poslugi-z-peredachi-elektrichnoyi-energiyi",
            ],
        }
        # fsync a temporary file before publishing it by exclusive hard link.
        # A crash cannot leave a partial request that later runs mistake for done.
        descriptor, temporary_name = tempfile.mkstemp(dir=output, prefix=".review-")
        temporary = Path(temporary_name)
        try:
            with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
                json.dump(request, handle, ensure_ascii=False, indent=2)
                handle.write("\n")
                handle.flush()
                os.fsync(handle.fileno())
            try:
                os.link(temporary, target)
                created = True
            except FileExistsError:
                created = False
        finally:
            temporary.unlink(missing_ok=True)
        requests.append({"site_id": identity, "request": str(target), "created": created})
    return requests


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--state-directory", required=True)
    args = parser.parse_args()
    today = dt.datetime.now(ZoneInfo("Europe/Kyiv")).date()
    print(json.dumps(prepare(args.config, args.state_directory, today), ensure_ascii=False))


if __name__ == "__main__":
    main()
