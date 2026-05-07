#!/usr/bin/env python3
"""Prototype CLI for Same Episode Duplicate Finder.

This is intentionally small and standard-library-only. It mirrors the current
C# parser, grouping, and scoring heuristics closely enough to validate a future
Python core without replacing the WinForms app yet.
"""

from __future__ import annotations

import argparse
import csv
import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, List, Optional


DEFAULT_IGNORED_EXTENSIONS = {
    ".ass",
    ".flac",
    ".mp3",
    ".m4a",
    ".aac",
    ".ogg",
    ".opus",
    ".wav",
    ".wma",
    ".alac",
    ".ape",
}


@dataclass
class EpisodeFile:
    key: str
    title: str
    episode: str
    subtitle_group: str
    version: str
    size_bytes: int
    size_mb: float
    file_name: str
    file_location: str
    path: str
    recommendation: str = ""
    confidence: str = ""
    review_status: str = ""
    recommendation_reason: str = ""


def normalize_title(title: Optional[str]) -> str:
    if title is None:
        return ""
    normalized = re.sub(r"[._-]+", " ", title)
    normalized = re.sub(r"\s+", " ", normalized)
    return normalized.strip()


def get_subtitle_group(file_name_or_base_name: str) -> str:
    match = re.match(r"^\[(?P<group>[^\]]+)\]\s*", file_name_or_base_name or "")
    return match.group("group").strip() if match else ""


def get_show_folder_title(path: Path, root: Path) -> str:
    try:
        relative_parent = path.parent.relative_to(root)
        parts = [
            part
            for part in relative_parent.parts
            if part.lower() not in {"uncen", "{complete}"}
            and not re.match(r"^season\s*\d+$", part, re.IGNORECASE)
        ]
    except ValueError:
        parts = []

    if parts:
        return normalize_title(parts[0])
    return normalize_title(path.parent.name)


def parse_file(path: Path, root: Path) -> Optional[EpisodeFile]:
    base_name = path.stem
    title = None
    episode_key = None
    subtitle_group = get_subtitle_group(base_name)

    sxe = re.match(
        r"^(?P<title>.*?)[ ._\-\[\(]*s(?P<season>\d{1,2})e(?P<episode>\d{1,3})(?:\D|$)",
        base_name,
        re.IGNORECASE,
    )
    if sxe:
        title = sxe.group("title")
        episode_key = f"S{int(sxe.group('season')):02d}E{int(sxe.group('episode')):02d}"
    else:
        anime = re.match(
            r"^(?:\[[^\]]+\]\s*)?(?P<title>.+)\s+-\s+(?P<episode>\d{1,4})(?:\s|\[|\(|$)",
            base_name,
        )
        if anime:
            title = anime.group("title")
            episode_key = f"E{int(anime.group('episode')):03d}"

    if episode_key is None:
        return None

    title = normalize_title(title)
    if not title:
        title = get_show_folder_title(path, root)

    version_match = re.search(r"\[(v\d+)\]", base_name, re.IGNORECASE)
    version = version_match.group(1).lower() if version_match else ""
    size_bytes = path.stat().st_size
    key = f"{title.lower()}|{episode_key}"

    return EpisodeFile(
        key=key,
        title=title,
        episode=episode_key,
        subtitle_group=subtitle_group,
        version=version,
        size_bytes=size_bytes,
        size_mb=round(size_bytes / (1024 * 1024), 2),
        file_name=path.name,
        file_location=str(path.parent),
        path=str(path),
    )


def extract_resolution(text: str) -> int:
    if not text:
        return 0
    if re.search(r"(?:^|[^0-9])4k(?:[^0-9]|$)", text, re.IGNORECASE):
        return 2160
    best = 0
    for match in re.finditer(
        r"(?:^|[^0-9])(?P<resolution>2160|1440|1080|720|576|480|360)\s*p?(?:[^0-9]|$)",
        text,
        re.IGNORECASE,
    ):
        best = max(best, int(match.group("resolution")))
    return best


def extract_version(version_text: str, file_text: str) -> int:
    best = 0
    for text in (version_text or "", file_text or ""):
        for match in re.finditer(r"(?:^|[^a-z0-9])v(?P<version>\d{1,2})(?:[^a-z0-9]|$)", text, re.IGNORECASE):
            best = max(best, int(match.group("version")))
    return best


def auto_keep_score(file: EpisodeFile) -> int:
    name = f"{file.file_name} {file.path}".lower()
    resolution = extract_resolution(name)
    version = extract_version(file.version, name)
    size_score = min(file.size_bytes // (1024 * 1024), 999_999)
    return resolution * 100_000_000 + version * 1_000_000 + size_score


def delete_confidence(score_gap: int) -> str:
    if score_gap >= 1_000_000:
        return "High"
    return "Medium" if score_gap > 0 else "Low"


def recommendation_reason(candidate: EpisodeFile, keep: EpisodeFile) -> str:
    reasons: List[str] = []
    candidate_name = f"{candidate.file_name} {candidate.path}".lower()
    keep_name = f"{keep.file_name} {keep.path}".lower()
    if extract_resolution(keep_name) > extract_resolution(candidate_name):
        reasons.append("kept file has higher resolution")
    if extract_version(keep.version, keep_name) > extract_version(candidate.version, candidate_name):
        reasons.append("kept file has newer version tag")
    if keep.size_bytes > candidate.size_bytes:
        reasons.append("kept file is larger")
    return "; ".join(reasons) + "." if reasons else "Lower quality score than recommended keep."


def apply_recommendations(files: Iterable[EpisodeFile]) -> None:
    by_key: dict[str, List[EpisodeFile]] = {}
    for file in files:
        by_key.setdefault(file.key.lower(), []).append(file)

    for group in by_key.values():
        if len(group) <= 1:
            for row in group:
                row.recommendation = "Review"
                row.confidence = "Low"
                row.review_status = "Needs review"
                row.recommendation_reason = "No duplicate peer was found in the current grouping."
            continue

        ranked = sorted(group, key=lambda row: (-auto_keep_score(row), -row.size_bytes, row.file_name.lower()))
        keep = ranked[0]
        keep_score = auto_keep_score(keep)
        keep.recommendation = "Keep"
        keep.confidence = "High"
        keep.review_status = "Best keep"
        keep.recommendation_reason = "Highest quality score in this duplicate group."

        for row in ranked[1:]:
            score_gap = keep_score - auto_keep_score(row)
            row.recommendation = "Delete"
            row.confidence = delete_confidence(score_gap)
            row.review_status = "Likely duplicate" if row.confidence == "High" else "Needs review"
            row.recommendation_reason = recommendation_reason(row, keep)


def scan(root: Path, ignored_extensions: set[str]) -> tuple[list[EpisodeFile], int, int]:
    parsed: list[EpisodeFile] = []
    visited = 0
    ignored = 0
    for dirpath, _, filenames in os.walk(root):
        for filename in filenames:
            visited += 1
            path = Path(dirpath) / filename
            if path.suffix.lower() in ignored_extensions:
                ignored += 1
                continue
            item = parse_file(path, root)
            if item is not None:
                parsed.append(item)
    return parsed, visited, ignored


def duplicate_rows(files: list[EpisodeFile]) -> list[EpisodeFile]:
    counts: dict[str, int] = {}
    for row in files:
        counts[row.key.lower()] = counts.get(row.key.lower(), 0) + 1
    duplicates = [row for row in files if counts.get(row.key.lower(), 0) > 1]
    return sorted(duplicates, key=lambda row: (row.title.lower(), row.episode.lower(), row.file_name.lower()))


def write_csv(path: Path, rows: Iterable[EpisodeFile]) -> None:
    with path.open("w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.writer(handle)
        writer.writerow(
            [
                "Recommendation",
                "Confidence",
                "ReviewStatus",
                "Reason",
                "Episode",
                "FileLocation",
                "EpisodeFile",
                "SubtitleGroup",
                "SizeMB",
                "Version",
                "Key",
                "Title",
                "SizeBytes",
                "Path",
            ]
        )
        for row in rows:
            writer.writerow(
                [
                    row.recommendation,
                    row.confidence,
                    row.review_status,
                    row.recommendation_reason,
                    row.episode,
                    row.file_location,
                    row.file_name,
                    row.subtitle_group,
                    row.size_mb,
                    row.version,
                    row.key,
                    row.title,
                    row.size_bytes,
                    row.path,
                ]
            )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Prototype CLI for Same Episode Duplicate Finder.")
    parser.add_argument("root", type=Path, help="Folder to scan.")
    parser.add_argument("--output", "-o", type=Path, default=Path("same-episode-duplicates.csv"), help="CSV output path.")
    parser.add_argument("--all", action="store_true", help="Write all parsed episodic files instead of duplicate candidates only.")
    parser.add_argument("--include-ext", action="append", default=[], help="Additional extension to include even if ignored by default, e.g. --include-ext .flac")
    parser.add_argument("--ignore-ext", action="append", default=[], help="Additional extension to ignore, e.g. --ignore-ext .txt")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    root = args.root.resolve()
    if not root.is_dir():
        raise SystemExit(f"Folder not found: {root}")

    ignored = set(DEFAULT_IGNORED_EXTENSIONS)
    ignored.update(ext.lower() if ext.startswith(".") else f".{ext.lower()}" for ext in args.ignore_ext)
    for ext in args.include_ext:
        ignored.discard(ext.lower() if ext.startswith(".") else f".{ext.lower()}")

    parsed, visited, ignored_count = scan(root, ignored)
    apply_recommendations(parsed)
    rows = parsed if args.all else duplicate_rows(parsed)
    write_csv(args.output, rows)

    duplicate_group_count = len({row.key.lower() for row in duplicate_rows(parsed)})
    print(
        f"Scan complete: {visited:,} visited | {len(parsed):,} parsed | "
        f"{ignored_count:,} ignored | {len(rows):,} written | {duplicate_group_count:,} duplicate groups."
    )
    print(f"Wrote {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
