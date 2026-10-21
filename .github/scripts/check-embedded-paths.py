#!/usr/bin/env python3
"""Fail if a shippable artifact has an absolute build path baked into it.

Compilers expand __FILE__ into string literals, so an unmapped build embeds the
absolute path of every source file it compiled -- the build machine's OS username and
directory layout, in a binary that gets published. Those literals live in __TEXT/.rdata,
not in debug info, so strip(1) does not remove them; the fix is at compile time (see
cmake/path-privacy.cmake) and this script is the gate that keeps it fixed.

Scans bytes, never text: BSD grep under a UTF-8 locale silently declines to match inside
binary files and reports a clean result for a leaking artifact. Archives (.nupkg, .zip,
.tar.gz) are expanded and their members scanned, so this can be pointed straight at a
packed package.

Usage:
    check-embedded-paths.py <path> [<path>...] [--allow REGEX]... [--quiet]

Exit status: 0 clean, 1 leaks found, 2 bad usage.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import tarfile
import tempfile
import zipfile

# Every leaked path observed so far sits under one of these roots. Matching on the root
# alone is deliberate: it catches the whole class (home dirs, scratch worktrees, tool
# caches) without this gate having to enumerate -- and therefore advertise -- what lives
# in any of them.
NEEDLES: tuple[bytes, ...] = (
    b"/Users/",
    b"/home/",
    b"/root/",
    b"C:\\Users\\",
    b"c:\\users\\",
)

# The checkout this script runs from. The roots above miss a clone outside any home
# directory (C:\dev\..., /opt/...), and native MSVC records __FILE__ with backslashes,
# so look for the repo's own root too: either separator, any case (Windows paths can
# show up with a lowercased drive letter and nothing else changed).
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def repo_root_needles(root: str) -> tuple[bytes, ...]:
    """Lowercased spellings of root plus a separator, matched against lowercased data.

    Kept even when a NEEDLES entry is a substring (a clone under C:\\Users\\...): those
    match case-sensitively, so c:\\Users\\... would slip past them. A root with fewer
    than two components (/src, /work, C:/repo) is skipped: it is generic enough to turn
    up inside unrelated paths, including the mapped /csvg/src/... ones, and would fail
    clean builds.
    """
    forward = root.replace("\\", "/").rstrip("/").lower()
    components = [part for part in forward.split("/") if part and not part.endswith(":")]
    if len(components) < 2:
        return ()
    backward = forward.replace("/", "\\")
    spellings = sorted({forward + "/", forward + "\\", backward + "\\", backward + "/"})
    return tuple(spelling.encode("utf-8", "surrogateescape") for spelling in spellings)


ROOT_NEEDLES = repo_root_needles(REPO_ROOT)

ARCHIVE_SUFFIXES = (".nupkg", ".snupkg", ".zip", ".tar.gz", ".tgz")

# Files whose bytes were actually read, archive members included. Used to reject a run
# that inspected nothing -- see main().
_inspected = 0

# Enough trailing context to identify the source of a hit, bounded so a binary full of
# garbage bytes cannot produce a wall of output.
SAMPLE_RE = re.compile(rb"[^\x00\s\"'<>]{0,120}")


def redact(sample: bytes) -> str:
    """Mask the user component so failing does not itself republish the leak.

    /Users/alice/Development/x -> /Users/***/Development/x
    """
    text = sample.decode("latin1")
    for needle in ROOT_NEEDLES:
        text = re.sub(re.escape(needle.decode("latin1")[:-1]), "<repo>", text, flags=re.IGNORECASE)
    return re.sub(
        r"(?i)((?:/Users/|/home/|C:\\Users\\))([^/\\]+)",
        lambda m: m.group(1) + "***",
        text,
    )


def scan_bytes(data: bytes) -> dict[bytes, tuple[int, list[str]]]:
    """Return {needle: (count, [redacted samples])} for every needle present in data."""
    hits: dict[bytes, tuple[int, list[str]]] = {}
    lowered = data.lower() if ROOT_NEEDLES else data
    for needle in NEEDLES + ROOT_NEEDLES:
        # Root needles are lowercase and searched case-insensitively; offsets line up
        # because bytes.lower() only touches ASCII letters.
        haystack = lowered if needle in ROOT_NEEDLES else data
        start = haystack.find(needle)
        if start < 0:
            continue
        samples: list[str] = []
        seen: set[str] = set()
        while start >= 0 and len(samples) < 3:
            if needle in ROOT_NEEDLES:
                # Replace the root by position rather than by pattern: SAMPLE_RE stops at
                # whitespace and 120 bytes, so a root with a space or a long one would only
                # be partly captured and then printed unredacted.
                rest = SAMPLE_RE.match(data, start + len(needle) - 1)
                sample = "<repo>" + redact(rest.group(0) if rest else b"")
            else:
                match = SAMPLE_RE.match(data, start)
                sample = redact(match.group(0) if match else needle)
            if sample not in seen:
                seen.add(sample)
                samples.append(sample)
            start = haystack.find(needle, start + len(needle))
        hits[needle] = (haystack.count(needle), samples)
    return hits


def iter_files(root: str):
    if os.path.isfile(root):
        yield root, os.path.basename(root)
        return
    for dirpath, _, filenames in os.walk(root):
        for name in sorted(filenames):
            path = os.path.join(dirpath, name)
            yield path, os.path.relpath(path, root)


def extract(path: str, dest: str) -> bool:
    """Expand an archive into dest. Returns False if it is not one we handle."""
    try:
        if zipfile.is_zipfile(path):
            with zipfile.ZipFile(path) as archive:
                archive.extractall(dest)
            return True
        if tarfile.is_tarfile(path):
            with tarfile.open(path) as archive:
                try:
                    # data filter: refuse absolute/parent paths in member names.
                    archive.extractall(dest, filter="data")
                except TypeError:  # Python < 3.12 has no filter parameter
                    archive.extractall(dest)
            return True
    except (OSError, zipfile.BadZipFile, tarfile.TarError) as exc:
        print(f"warning: could not expand {path}: {exc}", file=sys.stderr)
    return False


def check_file(
    path: str,
    label: str,
    allow: list[re.Pattern],
    only: list[re.Pattern],
    quiet: bool,
) -> int:
    """Scan one file (expanding it first if it is an archive). Returns leak count.

    --allow/--only match the path WITHIN the containing archive ("a.nupkg!lib/x.dll" ->
    "lib/x.dll"). Matching the whole label would let the package's own filename satisfy a
    filter meant for its contents -- e.g. --only 'Cs2VideoGenerator' would match every
    third-party assembly inside Cs2VideoGenerator.Cli.nupkg and scan the lot.
    """
    member = label.rsplit("!", 1)[-1]

    if any(pattern.search(member) for pattern in allow):
        if not quiet:
            print(f"  skip  {label} (allowlisted)")
        return 0

    # Archives are always expanded: --only applies to what is inside them, not to the
    # container.
    if label.lower().endswith(ARCHIVE_SUFFIXES):
        leaks = 0
        with tempfile.TemporaryDirectory() as tmp:
            if not extract(path, tmp):
                return 0
            for member, rel in iter_files(tmp):
                leaks += check_file(member, f"{label}!{rel}", allow, only, quiet)
        return leaks

    if only and not any(pattern.search(member) for pattern in only):
        if not quiet:
            print(f"  skip  {label} (not first-party)")
        return 0

    try:
        with open(path, "rb") as handle:
            data = handle.read()
    except OSError as exc:
        print(f"warning: could not read {path}: {exc}", file=sys.stderr)
        return 0

    global _inspected
    _inspected += 1

    hits = scan_bytes(data)
    if not hits:
        if not quiet:
            print(f"  ok    {label}")
        return 0

    for needle, (count, samples) in hits.items():
        shown = "<repo>" if needle in ROOT_NEEDLES else needle.decode("latin1")
        print(f"  LEAK  {label}: {count}x {shown}")
        for sample in samples:
            print(f"          {sample}")
    return len(hits)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="+", help="files, directories, or archives")
    parser.add_argument(
        "--allow",
        action="append",
        default=[],
        metavar="REGEX",
        help="skip artifacts whose path matches (repeatable); for third-party binaries "
        "that carry their own vendor's CI paths",
    )
    parser.add_argument(
        "--only",
        action="append",
        default=[],
        metavar="REGEX",
        help="scan only artifacts whose path matches (repeatable). Use when the target "
        "mixes first-party output with vendored third-party binaries, which carry their "
        "own vendor's CI paths and are not ours to fix",
    )
    parser.add_argument("--quiet", action="store_true", help="print only leaks")
    args = parser.parse_args()

    allow = [re.compile(pattern) for pattern in args.allow]
    only = [re.compile(pattern) for pattern in args.only]
    leaks = 0
    scanned = 0
    # A --only regex that matches nothing would scan zero artifacts and report success.
    # Track what actually got read so that failure is loud instead of vacuous.
    global _inspected
    _inspected = 0
    for root in args.paths:
        if not os.path.exists(root):
            print(f"error: no such path: {root}", file=sys.stderr)
            return 2
        print(f"scanning {root}")
        for path, rel in iter_files(root):
            scanned += 1
            leaks += check_file(path, rel, allow, only, args.quiet)

    if leaks:
        print(
            f"\n{leaks} embedded build path(s) found. These ship to users, and stripping "
            "does not remove them -- fix at compile time. See docs/build-path-privacy.md.",
            file=sys.stderr,
        )
        return 1

    if _inspected == 0:
        # Refuse to report success without having read anything: an --only regex that no
        # longer matches (a renamed artifact, a new RID) would otherwise turn this gate
        # into a no-op that still prints "clean".
        print(
            "\nerror: inspected 0 files. The paths exist but nothing matched --only "
            f"({', '.join(args.only) or 'none given'}); the scan proved nothing.",
            file=sys.stderr,
        )
        return 2

    print(f"\nclean: no embedded build paths ({_inspected} file(s) inspected).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
