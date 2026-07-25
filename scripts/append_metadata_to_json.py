#!/usr/bin/env python3
"""Append FModel package metadata into exported property JSON files (post-export).

FModel convention (this fork):
  - Properties:  Foo.json          under PropertiesDirectory (often .../Output/Exports)
  - Sidecar:     Foo.metadata.json  same folder (Metadata Export = Auto Export)
  - Append mode: wraps as {"Exports": <props>, "Metadata": <package>} (indent=2)

This script merges existing .metadata.json sidecars into property JSONs so you do not
need to re-export with "Append to Json". Per-file merge is multithreaded (I/O-bound).

Examples:
  # Sidecars sit next to property JSONs (default FModel Auto Export layout)
  python scripts/append_metadata_to_json.py "Z:\\Datamining\\Arc Raiders\\Output\\Exports"

  # Metadata files live in a separate tree (same relative paths / basenames)
  python scripts/append_metadata_to_json.py "Z:\\...\\Exports" --metadata-root "Z:\\...\\Metadata"

  # Preview only; write Foo.withmeta.json instead of overwriting
  python scripts/append_metadata_to_json.py "Z:\\...\\Exports" --dry-run
  python scripts/append_metadata_to_json.py "Z:\\...\\Exports" --alongside

  # Cap worker threads (default: max(1, cpu_count - 1))
  python scripts/append_metadata_to_json.py "Z:\\...\\Exports" -j 8

  # After a successful merge, delete the .metadata.json sidecar that was used
  python scripts/append_metadata_to_json.py "Z:\\...\\Exports" --delete-metadata
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import threading
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Any


METADATA_SUFFIX = ".metadata.json"
WITHMETA_SUFFIX = ".withmeta.json"
DEFAULT_KEY = "Metadata"
EXPORTS_KEY = "Exports"


def default_workers() -> int:
    """Leave one core free when possible (matches FModel export auto DOP)."""
    cpu = os.cpu_count() or 1
    return max(1, cpu - 1)


def is_metadata_file(path: Path) -> bool:
    name = path.name.lower()
    return name.endswith(METADATA_SUFFIX) or name.endswith(WITHMETA_SUFFIX)


def load_json(path: Path) -> Any:
    # utf-8-sig tolerates a BOM (PowerShell Set-Content / some editors); FModel writes plain UTF-8.
    with path.open("r", encoding="utf-8-sig") as f:
        return json.load(f)


def dump_json(path: Path, data: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(data, indent=2, ensure_ascii=False)
    # Match typical pretty-print trailing newline from editors / Newtonsoft dumps.
    if not text.endswith("\n"):
        text += "\n"
    with path.open("w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def property_stem(path: Path) -> str:
    """Stem for Foo.json -> Foo (not for Foo.metadata.json)."""
    return path.stem


def sibling_metadata_path(prop_path: Path) -> Path:
    return prop_path.with_name(f"{property_stem(prop_path)}{METADATA_SUFFIX}")


def build_metadata_index(metadata_root: Path) -> dict[str, Path]:
    """Map relative posix path (sans .metadata.json) and basename -> metadata file.

    Later files with the same key overwrite earlier ones; basename is a fallback
    when directory layout differs between export and metadata trees.
    """
    by_rel: dict[str, Path] = {}
    by_base: dict[str, Path] = {}
    for path in metadata_root.rglob("*"):
        if not path.is_file():
            continue
        if not path.name.lower().endswith(METADATA_SUFFIX):
            continue
        rel = path.relative_to(metadata_root).as_posix()
        # Strip trailing ".metadata.json" (case-insensitive)
        lower = rel.lower()
        if lower.endswith(METADATA_SUFFIX):
            key = rel[: -len(METADATA_SUFFIX)]
        else:
            key = rel.rsplit(".", 1)[0]
        by_rel[key.replace("\\", "/")] = path
        by_base[path.name[: -len(METADATA_SUFFIX)] if path.name.lower().endswith(METADATA_SUFFIX) else path.stem] = path
        # Also index bare filename stem: Foo.metadata.json -> Foo
        by_base[Path(key).name] = path
    # Prefer relative keys; basename fallback is separate.
    return {**{f"base:{k}": v for k, v in by_base.items()}, **{f"rel:{k}": v for k, v in by_rel.items()}}


def resolve_metadata(
    prop_path: Path,
    export_root: Path,
    metadata_root: Path | None,
    index: dict[str, Path] | None,
) -> Path | None:
    sibling = sibling_metadata_path(prop_path)
    if sibling.is_file():
        return sibling

    if metadata_root is None or index is None:
        return None

    try:
        rel = prop_path.relative_to(export_root).as_posix()
    except ValueError:
        rel = prop_path.name

    stem_rel = rel[:-5] if rel.lower().endswith(".json") else str(Path(rel).with_suffix(""))
    stem_rel = stem_rel.replace("\\", "/")

    for key in (f"rel:{stem_rel}", f"base:{property_stem(prop_path)}", f"base:{Path(stem_rel).name}"):
        hit = index.get(key)
        if hit is not None and hit.is_file():
            return hit
    return None


def merge_metadata(
    props: Any,
    metadata: Any,
    key: str,
    *,
    wrap_exports: bool,
    force: bool,
) -> tuple[Any, str]:
    """
    Return (merged_document, action) where action is 'merged' | 'replaced' | 'skipped'.

    Matches FModel AppendToJson when the property root is not already an object
    with the metadata key: wrap as {Exports: props, Metadata: metadata}.
    """
    if isinstance(props, dict):
        if key in props and not force:
            return props, "skipped"
        if wrap_exports and EXPORTS_KEY not in props:
            # Force the AppendToJson shape even for object roots.
            out = {EXPORTS_KEY: props, key: metadata}
            return out, "merged"
        out = dict(props)
        action = "replaced" if key in out else "merged"
        out[key] = metadata
        return out, action

    # Array / scalar roots: FModel AppendToJson always wraps under Exports.
    out = {EXPORTS_KEY: props, key: metadata}
    return out, "merged"


def iter_property_jsons(export_root: Path):
    for path in export_root.rglob("*.json"):
        if not path.is_file():
            continue
        if is_metadata_file(path):
            continue
        yield path


class MergeStats:
    """Thread-safe counters for merge progress."""

    __slots__ = (
        "_lock",
        "scanned",
        "no_meta",
        "merged",
        "replaced",
        "skipped",
        "deleted",
        "errors",
    )

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self.scanned = 0
        self.no_meta = 0
        self.merged = 0
        self.replaced = 0
        self.skipped = 0
        self.deleted = 0
        self.errors = 0

    def bump(self, name: str, n: int = 1) -> None:
        with self._lock:
            setattr(self, name, getattr(self, name) + n)

    def summary_line(self, *, dry_run: bool, delete_metadata: bool) -> tuple[str, int]:
        with self._lock:
            line = (
                "\nDone. "
                f"scanned={self.scanned} merged={self.merged} replaced={self.replaced} "
                f"skipped={self.skipped} no_metadata={self.no_meta} errors={self.errors}"
            )
            if delete_metadata:
                line += f" deleted_metadata={self.deleted}"
            if dry_run:
                line += " (dry-run)"
            errors = self.errors
        return line, errors


def process_one(
    prop_path: Path,
    export_root: Path,
    metadata_root: Path | None,
    index: dict[str, Path] | None,
    *,
    key: str,
    wrap_exports: bool,
    force: bool,
    alongside: bool,
    dry_run: bool,
    delete_metadata: bool,
    print_lock: threading.Lock,
    stats: MergeStats,
) -> None:
    """Merge one property JSON with its sidecar. Owns a unique output path."""
    stats.bump("scanned")
    meta_path = resolve_metadata(prop_path, export_root, metadata_root, index)
    if meta_path is None:
        stats.bump("no_meta")
        return

    try:
        props = load_json(prop_path)
    except (OSError, json.JSONDecodeError) as e:
        stats.bump("errors")
        with print_lock:
            print(f"error: cannot load property JSON {prop_path}: {e}", file=sys.stderr)
        return

    try:
        metadata = load_json(meta_path)
    except (OSError, json.JSONDecodeError) as e:
        stats.bump("errors")
        with print_lock:
            print(f"error: cannot load metadata JSON {meta_path}: {e}", file=sys.stderr)
        return

    # Skip only when root is a non-dict AND we somehow refuse wrap — we always wrap
    # arrays to match FModel. Log odd scalar roots but still wrap them.
    if not isinstance(props, (dict, list)):
        with print_lock:
            print(
                f"warn: non-object/non-array root in {prop_path}; wrapping under Exports",
                file=sys.stderr,
            )

    merged, action = merge_metadata(
        props,
        metadata,
        key,
        wrap_exports=wrap_exports,
        force=force,
    )
    if action == "skipped":
        stats.bump("skipped")
        with print_lock:
            print(f"skip (key exists): {prop_path}")
        return

    if alongside:
        out_path = prop_path.with_name(f"{property_stem(prop_path)}{WITHMETA_SUFFIX}")
    else:
        out_path = prop_path

    try:
        rel_out = out_path.relative_to(export_root)
    except ValueError:
        rel_out = out_path

    if dry_run:
        with print_lock:
            msg = f"dry-run [{action}]: {prop_path.name} + {meta_path.name} -> {rel_out}"
            if delete_metadata:
                msg += f" (would delete {meta_path.name})"
            print(msg)
    else:
        try:
            dump_json(out_path, merged)
        except OSError as e:
            stats.bump("errors")
            with print_lock:
                print(f"error: cannot write {out_path}: {e}", file=sys.stderr)
            return
        with print_lock:
            print(f"{action}: {rel_out}")

        if delete_metadata:
            try:
                meta_path.unlink()
            except OSError as e:
                stats.bump("errors")
                with print_lock:
                    print(f"error: cannot delete metadata {meta_path}: {e}", file=sys.stderr)
                # Merge already succeeded; still count the merge action below.
            else:
                stats.bump("deleted")
                with print_lock:
                    print(f"deleted: {meta_path.name}")

    stats.bump(action)


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(
        description=(
            "Merge FModel .metadata.json sidecars into exported property JSON files "
            "(post-export Append to Json). Per-file work runs in a thread pool."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Sidecar name: <assetStem>.metadata.json next to <assetStem>.json\n"
            "Merge key default: Metadata (same as FModel Append to Json)\n"
            "Non-object property roots are wrapped as "
            '{"Exports": <root>, "Metadata": <meta>}.\n'
            f"Workers default: max(1, cpu_count - 1) [= {default_workers()} on this machine]."
        ),
    )
    p.add_argument(
        "export_root",
        type=Path,
        help="Root folder of exported property JSONs (e.g. Properties/Exports directory)",
    )
    p.add_argument(
        "--metadata-root",
        type=Path,
        default=None,
        help="Optional separate tree of *.metadata.json files (matched by relative path then basename)",
    )
    p.add_argument(
        "--key",
        default=DEFAULT_KEY,
        help=f'JSON object key for metadata (default: "{DEFAULT_KEY}")',
    )
    p.add_argument(
        "--alongside",
        action="store_true",
        help="Write <name>.withmeta.json instead of overwriting the property JSON",
    )
    p.add_argument(
        "--dry-run",
        action="store_true",
        help="Report what would be written without modifying files",
    )
    p.add_argument(
        "--delete-metadata",
        action="store_true",
        help=(
            "After a successful merge write, delete the .metadata.json sidecar that was used "
            "(no-op on skip/error; with --dry-run only logs that it would delete)"
        ),
    )
    p.add_argument(
        "--force",
        action="store_true",
        help="Replace an existing metadata key if already present",
    )

    p.add_argument(
        "--wrap-exports",
        action="store_true",
        help=(
            "Always wrap object-rooted property JSON under \"Exports\" "
            "(array roots are always wrapped, matching FModel Append to Json)"
        ),
    )
    p.add_argument(
        "-j",
        "--workers",
        type=int,
        default=None,
        metavar="N",
        help=(
            "Thread pool size for per-file merges "
            f"(default: max(1, cpu_count - 1) = {default_workers()})"
        ),
    )
    return p.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    export_root: Path = args.export_root.resolve()
    if not export_root.is_dir():
        print(f"error: export root is not a directory: {export_root}", file=sys.stderr)
        return 2

    workers = args.workers if args.workers is not None else default_workers()
    if workers < 1:
        print(f"error: --workers must be >= 1 (got {workers})", file=sys.stderr)
        return 2

    metadata_root: Path | None = None
    index: dict[str, Path] | None = None
    if args.metadata_root is not None:
        metadata_root = args.metadata_root.resolve()
        if not metadata_root.is_dir():
            print(f"error: metadata root is not a directory: {metadata_root}", file=sys.stderr)
            return 2
        index = build_metadata_index(metadata_root)

    stats = MergeStats()
    print_lock = threading.Lock()
    paths = list(iter_property_jsons(export_root))

    def _task(prop_path: Path) -> None:
        process_one(
            prop_path,
            export_root,
            metadata_root,
            index,
            key=args.key,
            wrap_exports=args.wrap_exports,
            force=args.force,
            alongside=args.alongside,
            dry_run=args.dry_run,
            delete_metadata=args.delete_metadata,
            print_lock=print_lock,
            stats=stats,
        )

    with ThreadPoolExecutor(max_workers=workers) as pool:
        futures = [pool.submit(_task, path) for path in paths]
        for fut in as_completed(futures):
            # Surface unexpected worker exceptions as errors.
            exc = fut.exception()
            if exc is not None:
                stats.bump("errors")
                with print_lock:
                    print(f"error: worker failed: {exc}", file=sys.stderr)

    summary, errors = stats.summary_line(
        dry_run=args.dry_run,
        delete_metadata=args.delete_metadata,
    )
    print(summary)
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
