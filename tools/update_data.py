#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Regenerates Resources/DEFAULT_DATA.json from the tarkov.dev GraphQL API.

Mirrors TarkovMarketJob.cs exactly — same query, same transformations,
same output schema — so the embedded fallback stays in sync with what
the app would fetch at runtime.

Also prints audit tables for:
  - Map nameIds (compare against MapNames.cs)
  - Boss list from tarkov.dev GitHub (compare against _aiRolesByVoice)
  - Thermal/NVG items (compare against GearManager.cs hardcoded IDs)

Usage:
    python tools/update_data.py
"""

import sys
import math
import json
from pathlib import Path

import requests

# Force UTF-8 output on Windows consoles that default to cp1252
if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
    import io
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

GRAPHQL_URL = "https://api.tarkov.dev/graphql"
BOSSES_URL  = "https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/bosses.json"

REPO_ROOT   = Path(__file__).parent.parent
OUTPUT_PATH = REPO_ROOT / "Resources" / "DEFAULT_DATA.json"

# ─── GraphQL query — exact copy of TarkovDevCore.cs ──────────────────────────

QUERY = """
{
  maps {
    name
    nameId
    extracts {
      name
      faction
      position { x, y, z }
    }
    transits {
      description
      position { x, y, z }
    }
  }
  items {
    id
    name
    shortName
    width
    height
    sellFor {
      vendor { name }
      priceRUB
    }
    basePrice
    avg24hPrice
    historicalPrices { price }
    categories { name }
    iconLink
    iconLinkFallback
    imageLink
    properties {
      ... on ItemPropertiesWeapon { caliber }
    }
  }
  questItems {
    id
    shortName
  }
  lootContainers {
    id
    normalizedName
    name
  }
  tasks {
    id
    name
    trader { name }
    kappaRequired
    map {
      id
      normalizedName
      name
    }
    objectives {
      id
      type
      optional
      description
      maps { id name normalizedName }
      ... on TaskObjectiveItem {
        item { id name shortName }
        zones {
          id
          map { id normalizedName name }
          position { y x z }
        }
        requiredKeys { id name shortName }
        count
        foundInRaid
      }
      ... on TaskObjectiveMark {
        id description
        markerItem { id name shortName }
        maps { id normalizedName name }
        zones {
          id
          map { id normalizedName name }
          position { y x z }
        }
        requiredKeys { id name shortName }
      }
      ... on TaskObjectiveQuestItem {
        id description
        requiredKeys { id name shortName }
        maps { id normalizedName name }
        zones {
          id
          map { id normalizedName name }
          position { y x z }
        }
        questItem { id name shortName normalizedName description }
        count
      }
      ... on TaskObjectiveBasic {
        id description
        requiredKeys { id name shortName }
        maps { id normalizedName name }
        zones {
          id
          map { id normalizedName name }
          position { y x z }
        }
      }
      ... on TaskObjectiveShoot {
        maps { id normalizedName name }
        zones {
          id
          map { id normalizedName name }
          outline { x y z }
          position { y x z }
        }
      }
    }
    taskRequirements {
      task { id }
      status
    }
  }
  traders {
    id
    name
  }
}
"""

# ─── Flea tax — exact port of FleaTax.cs ─────────────────────────────────────

def _flea_tax(requirements_price: float, base_price: float) -> float:
    """
    Mirrors FleaTax.Calculate() from TarkovMarket/FleaTax.cs.
    CommunityItemTax = CommunityRequirementTax = 3, RagFairCommissionModifier = 1.
    """
    if base_price == 0 or requirements_price == 0:
        return 0.0
    num2 = 0.03
    num3 = 0.03
    num4 = math.log10(base_price / requirements_price)
    num5 = math.log10(requirements_price / base_price)
    if requirements_price >= base_price:
        num5 = num5 ** 1.08
    else:
        num4 = num4 ** 1.08
    return base_price * num2 * (4.0 ** num4) + requirements_price * num3 * (4.0 ** num5)


def _optimal_flea(item: dict) -> int:
    """Mirrors ApiItemElement.OptimalFleaPrice."""
    base = item.get("basePrice") or 0
    if base == 0:
        return 0
    avg = item.get("avg24hPrice")
    if avg and _flea_tax(avg, base) < avg:
        return int(avg)
    hist = [
        h["price"] for h in item.get("historicalPrices", [])
        if h.get("price") and _flea_tax(h["price"], base) < h["price"]
    ]
    return int(sum(hist) / len(hist)) if hist else 0


def _best_vendor(item: dict) -> tuple[int, str]:
    """Mirrors ApiItemElement.HighestVendorPrice + BestVendorName."""
    best, name = 0, ""
    for s in item.get("sellFor", []):
        vname = (s.get("vendor") or {}).get("name", "")
        price = s.get("priceRUB")
        if vname and vname != "Flea Market" and price and price > best:
            best, name = price, vname
    return best, name


# ─── Main ─────────────────────────────────────────────────────────────────────

def main() -> int:
    # 1. GraphQL fetch
    print(f"[INFO] Querying {GRAPHQL_URL} …")
    try:
        resp = requests.post(GRAPHQL_URL, json={"query": QUERY}, timeout=120)
        resp.raise_for_status()
    except Exception as ex:
        print(f"[ERR ] GraphQL request failed: {ex}", file=sys.stderr)
        return 1

    payload = resp.json()
    if "errors" in payload:
        print(f"[ERR ] GraphQL errors: {payload['errors']}", file=sys.stderr)
        return 1

    data = payload["data"]

    # 2. Build items list (regular + quest items + static containers)
    items_out: list[dict] = []

    for item in data["items"]:
        trader_price, trader_name = _best_vendor(item)
        items_out.append({
            "bsgID":            item["id"],
            "name":             item["name"],
            "shortName":        item["shortName"],
            "price":            trader_price,
            "traderName":       trader_name,
            "fleaPrice":        _optimal_flea(item),
            "slots":            item["width"] * item["height"],
            "categories":       [c["name"] for c in item.get("categories", [])],
            "iconLink":         item.get("iconLink") or None,
            "iconLinkFallback": item.get("iconLinkFallback") or None,
            "imageLink":        item.get("imageLink") or None,
            "caliber":          (item.get("properties") or {}).get("caliber"),
        })

    for qi in data["questItems"]:
        items_out.append({
            "bsgID":            qi["id"],
            "name":             f"Q_{qi['shortName']}",
            "shortName":        f"Q_{qi['shortName']}",
            "price":            -1,
            "traderName":       "",
            "fleaPrice":        -1,
            "slots":            1,
            "categories":       ["Quest Item"],
            "iconLink":         None,
            "iconLinkFallback": None,
            "imageLink":        None,
            "caliber":          None,
        })

    for c in data["lootContainers"]:
        items_out.append({
            "bsgID":            c["id"],
            "name":             c["normalizedName"],
            "shortName":        c["name"],
            "price":            -1,
            "traderName":       "",
            "fleaPrice":        -1,
            "slots":            1,
            "categories":       ["Static Container"],
            "iconLink":         None,
            "iconLinkFallback": None,
            "imageLink":        None,
            "caliber":          None,
        })

    # 3. Build maps list
    maps_out = [
        {
            "name":     m["name"],
            "nameId":   m["nameId"],
            "extracts": [
                {
                    "name":     e["name"],
                    "faction":  e["faction"],
                    "position": e.get("position"),
                }
                for e in m.get("extracts", [])
            ],
            "transits": [
                {
                    "description": t["description"],
                    "position":    t.get("position"),
                }
                for t in m.get("transits", [])
            ],
        }
        for m in data["maps"]
    ]

    # 4. Traders (pass-through)
    traders_out = [
        {"id": t["id"], "name": t["name"]}
        for t in data["traders"]
        if t.get("id") and t.get("name")
    ]

    result = {
        "items":   items_out,
        "tasks":   data["tasks"],
        "maps":    maps_out,
        "traders": traders_out,
    }

    # 5. Write output
    print(f"[INFO] Writing {OUTPUT_PATH} …")
    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT_PATH.write_text(
        json.dumps(result, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )
    size_mb = OUTPUT_PATH.stat().st_size / 1024 / 1024
    print(
        f"[INFO] Done — {size_mb:.1f} MB | "
        f"{len(data['items'])} items + {len(data['questItems'])} quest items + "
        f"{len(data['lootContainers'])} containers = {len(items_out)} total | "
        f"{len(maps_out)} maps | {len(data['tasks'])} tasks"
    )

    # ── Audit 1: map nameIds ──────────────────────────────────────────────────
    print("\n── Map nameIds in API response (compare against MapNames.cs) ──")
    for m in sorted(maps_out, key=lambda x: x["nameId"]):
        print(f"  {m['nameId']!r:30s}  {m['name']}")

    # ── Audit 2: boss list from tarkov.dev GitHub ─────────────────────────────
    print("\n── Boss list from tarkov.dev GitHub (compare against _aiRolesByVoice) ──")
    try:
        br = requests.get(BOSSES_URL, timeout=30)
        br.raise_for_status()
        bosses = br.json()
        for b in bosses:
            print(f"  {b.get('normalizedName', '?')}")
    except Exception as ex:
        print(f"  (fetch failed: {ex})")

    # ── Audit 3: thermal / NVG items ─────────────────────────────────────────
    thermal_keywords = {"Thermal Scope", "Thermal Vision"}
    nvg_keywords     = {"Night Vision", "NVG"}

    thermals = [i for i in items_out if any(c in thermal_keywords for c in i["categories"])]
    nvgs     = [i for i in items_out if any(c in nvg_keywords     for c in i["categories"])]

    print(f"\n── Thermal items ({len(thermals)}) — compare against GearManager.cs ThermalIds ──")
    for i in thermals:
        print(f"  \"{i['bsgID']}\",  // {i['shortName']}")

    print(f"\n── NVG items ({len(nvgs)}) — compare against GearManager.cs NvgIds ──")
    for i in nvgs:
        print(f"  \"{i['bsgID']}\",  // {i['shortName']}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
