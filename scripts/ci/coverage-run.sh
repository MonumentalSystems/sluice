#!/usr/bin/env bash
# Sluice coverage run for the uranus gnostr-cloud instance.
#
# Mirrors the fleet coverage pattern (hyades / orleans-rust-client): `dotnet test
# --collect "XPlat Code Coverage"` (coverlet -> cobertura), adapt the cobertura
# into the cargo-llvm-cov-shaped {filename,summary,segments} report the /coverage
# UI renders, then:
#   (a) emit the `##gnostr-cloud-coverage:NN.NN##` wall-badge marker on STDOUT
#       (the runner scrapes stdout to fill the CI "COVERAGE" column), and
#   (b) POST the report + packed sources to /api/ci/coverage/upload for the
#       per-file drilldown (NON-FATAL — a CAS hiccup must not red a green run).
#
# Sluice is pure .NET (no Rust half). PUBLIC repo: the upload host + token come
# ONLY from the runner-injected context env — NOTHING internal is hardcoded here
# (the secret-scan gate forbids private-range coordinates in the tree).
set -e
WS="${GITHUB_WORKSPACE}"; [ -d "$WS" ] || WS="$PWD"; cd "$WS"

export HOME=/tmp DOTNET_CLI_HOME=/tmp NUGET_PACKAGES=/tmp/nuget \
       DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
echo "dotnet: $(dotnet --version 2>&1)"

export MARKERS="$WS/.gnostr-cloud-ci-output.log"; : > "$MARKERS"
# The runner writes the coverage CAS host + per-job upload token into a context
# env file; source it when present.
if [ -f "$WS/.gnostr-cloud-ci-context.env" ]; then . "$WS/.gnostr-cloud-ci-context.env"; fi

# ── .NET: dotnet test + coverlet (cobertura) ─────────────────────────────────
# Fast unit subset only (the gated live-barkd integration tests Skip without
# BARKD_TEST_*). coverlet.collector is referenced by the test csproj.
rm -rf /tmp/cov && mkdir -p /tmp/cov
dotnet test tests/Sluice.Tests/Sluice.Tests.csproj -c Release \
  --collect:"XPlat Code Coverage" \
  --results-directory /tmp/cov \
  --filter "Speed!=Slow" \
  --logger "console;verbosity=minimal" || \
  echo "::warning::some .NET tests failed; emitting coverage from collected data"

COB=$(find /tmp/cov -name "*.cobertura.xml" | head -1)
[ -n "$COB" ] && echo "cobertura: $COB" || { echo "::error::no cobertura coverage file produced" >&2; exit 1; }

# ── cobertura → cov-report.json (+ packed sources) and emit the combined % ────
python3 - "$COB" "$WS" /tmp/cov-report.json /tmp/cov-sources.json "$MARKERS" <<'PY'
import sys, os, json, base64, re
import xml.etree.ElementTree as ET

cob_path, workspace, report_out, sources_out, markers = sys.argv[1:6]
PER_FILE_CAP = 1024 * 1024          # 1 MiB/file
TOTAL_CAP    = 24 * 1024 * 1024     # 24 MiB total packed sources

def adapt_cobertura(path):
    root = ET.parse(path).getroot()
    source_roots = [s.text for s in root.findall("sources/source") if s.text] or [workspace]

    ws_index = {}
    if workspace and os.path.isdir(workspace):
        for dp, _, fns in os.walk(workspace):
            if any(seg in dp for seg in ("/bin/", "/obj/", "/.git/", "/.nuget/")):
                continue
            for fn in fns:
                full = os.path.join(dp, fn)
                ws_index.setdefault(os.path.relpath(full, workspace), full)
                ws_index.setdefault(fn, full)

    def resolve(fn):
        if os.path.isabs(fn) and os.path.exists(fn): return fn
        for r in source_roots:
            p = os.path.join(r, fn)
            if os.path.exists(p): return p
        p = os.path.join(workspace, fn)
        if os.path.exists(p): return p
        parts = fn.lstrip("/").split(os.sep)
        for cut in range(len(parts)):
            key = os.sep.join(parts[cut:])
            if key in ws_index: return ws_index[key]
        return ws_index.get(os.path.basename(fn))

    # cobertura emits one <class> per C# type, so a .cs file with N types shows up
    # N times. Dedup by resolved file, merging per-line hits (covered wins).
    line_hits, methods = {}, {}
    for cls in root.findall(".//classes/class"):
        fn = cls.get("filename", "")
        if not fn: continue
        # Don't count test code toward library coverage.
        if re.search(r'(\.Tests/|\.IntegrationTests/|/obj/|/bin/)', fn): continue
        abs_fn = resolve(fn) or os.path.join(workspace, fn)
        lh = line_hits.setdefault(abs_fn, {})
        for ln in cls.findall("lines/line"):
            try:
                n, h = int(ln.get("number", "0")), int(ln.get("hits", "0"))
            except ValueError:
                continue
            prev = lh.get(n)                       # `is None` (not `or`): keep h=0 first sightings
            if prev is None or h > prev: lh[n] = h
        ms = methods.setdefault(abs_fn, [])
        for m in cls.findall("methods/method"):
            ms.append(any(int(l.get("hits", "0")) > 0 for l in m.findall("lines/line")))

    out = []
    for abs_fn, lh in line_hits.items():
        lc, lcov = len(lh), sum(1 for h in lh.values() if h > 0)
        ms = methods.get(abs_fn, [])
        mc, mcov = len(ms), sum(1 for c in ms if c)
        lpct = (lcov / lc * 100) if lc else 0.0
        out.append({
            "filename": abs_fn,
            "language": "csharp",
            "summary": {
                "lines":     {"count": lc, "covered": lcov, "percent": lpct},
                "regions":   {"count": lc, "covered": lcov, "percent": lpct},
                "functions": {"count": mc, "covered": mcov, "percent": (mcov/mc*100) if mc else 0.0},
                "branches":  {"count": 0, "covered": 0, "percent": 0.0},
            },
            "segments": [[n, 1, h, True, False, False] for n, h in sorted(lh.items())],
        })
    return out

files = []
try:
    files.extend(adapt_cobertura(cob_path))
except Exception as e:                              # noqa: BLE001 — never let parsing kill the report
    print(f"::warning::cobertura adapt failed: {e}", file=sys.stderr)

def s(f, k, field): return f["summary"][k][field]
tot = {k: {"count": sum(s(f, k, "count") for f in files),
           "covered": sum(s(f, k, "covered") for f in files)}
       for k in ("lines", "regions", "functions", "branches")}
for k, v in tot.items():
    v["percent"] = (v["covered"] / v["count"] * 100) if v["count"] else 0.0

json.dump({"data": [{"files": files, "totals": tot}]}, open(report_out, "w"))

# Pack source files for the drilldown.
sources, packed_total = {}, 0
oversize = total_cap = missing = 0
for f in files:
    fn = f["filename"]
    if not os.path.exists(fn): missing += 1; continue
    try:
        size = os.path.getsize(fn)
        if size > PER_FILE_CAP: oversize += 1; continue
        if packed_total + size > TOTAL_CAP: total_cap += 1; continue
        sources[fn] = base64.b64encode(open(fn, "rb").read()).decode()
        packed_total += size
    except OSError:
        missing += 1
json.dump(sources, open(sources_out, "w"))

pct = tot["lines"]["percent"]
msg = (f"{len(files)} files — lines {tot['lines']['covered']}/{tot['lines']['count']} "
       f"({pct:.2f}%); sources packed {len(sources)} ({packed_total}B) "
       f"oversize={oversize} total_cap={total_cap} missing={missing}")
print(f"::notice::{msg}", file=sys.stderr)
with open(markers, "a") as m:
    m.write(f"##gnostr-cloud-coverage:{pct:.2f}##\n")
    m.write(f"##gnostr-cloud-coverage-adapter:{msg}##\n")
# Emit the wall-badge marker to STDOUT too — the runner scans stdout for
# ##gnostr-cloud-coverage:NN## (the markers FILE alone is NOT picked up).
print(f"##gnostr-cloud-coverage:{pct:.2f}##")
print(f"LINE COVERAGE: {pct:.2f}%")
PY

# ── Upload the drilldown report + sources (NON-FATAL) ────────────────────────
# Host + token are injected by the runner agent (context env); nothing internal
# is hardcoded. If either is absent the % wall-badge above still stands.
HOST="${GNOSTR_CLOUD_CI_HOST:-}"
if [ -z "${GNOSTR_CLOUD_CI_COVERAGE_UPLOAD_TOKEN:-}" ] || [ -z "$HOST" ]; then
  echo "::warning::coverage CAS upload skipped — upload host/token not provided by the runner" >&2; exit 0
fi
UPLOAD="${HOST%/}/api/ci/coverage/upload"
AUTH="Authorization: Bearer ${GNOSTR_CLOUD_CI_COVERAGE_UPLOAD_TOKEN}"
up() { # up <kind> <file>
  local rc; rc=$(curl -sS --max-time 90 -o /tmp/up-$1.out -w '%{http_code}' \
    -X POST -H "$AUTH" -H 'Content-Type: application/json' \
    --data-binary @"$2" "${UPLOAD}?kind=$1" 2>/tmp/up-$1.err || echo 000)
  case "$rc" in
    200|201|204) echo "coverage CAS upload ($1): HTTP $rc OK" ;;
    *) echo "::warning::coverage CAS upload ($1) HTTP $rc — $(head -c 200 /tmp/up-$1.out 2>/dev/null)$(head -c 200 /tmp/up-$1.err 2>/dev/null)" >&2 ;;
  esac
}
up report  /tmp/cov-report.json
up sources /tmp/cov-sources.json
exit 0
