#!/usr/bin/env python3
"""Install per-user daily backups or activate a verified ZIP while AgMemory clients are offline."""
import argparse
import datetime
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import uuid

from desktop_support import read_installation

LABEL = "com.agmemory.daily-backup"
DEFAULT_DATA = Path.home() / "Library/Application Support/AgMemory"
DEFAULT_BACKUPS = Path.home() / "Library/Application Support/AgMemory-Backups/automatic"


def checked_path(value):
    """Reject linked path components instead of silently redirecting backups or restore destinations."""
    path = Path(os.path.abspath(value))
    for part in (path, *path.parents):
        if part.is_symlink():
            raise ValueError("Symbolic links are not supported for backup paths")
    return path


def make_plist(binary, data, backups):
    """Build a shell-free daily 03:00 local schedule plus a catch-up attempt at login/load."""
    return {
        "Label": LABEL,
        "ProgramArguments": [str(binary), "--backup", "--data-dir", str(data), "--backup-dir", str(backups)],
        "StartCalendarInterval": {"Hour": 3, "Minute": 0},
        "RunAtLoad": True,
        "ProcessType": "Background",
        "LowPriorityIO": True,
        "StandardOutPath": str(backups / "last-run.log"),
        "StandardErrorPath": str(backups / "last-error.log"),
    }


def install(binary, data, backups):
    """Atomically install a private LaunchAgent, preserving a prior plist before replacing the schedule."""
    backups.mkdir(mode=0o700, parents=True, exist_ok=True)
    for name in ("last-run.log", "last-error.log"):
        log = backups / name
        if log.is_symlink():
            raise ValueError("Log path must not be a symbolic link")
        log.touch(mode=0o600, exist_ok=True)
        log.chmod(0o600)
    agent = checked_path(Path.home() / "Library/LaunchAgents" / (LABEL + ".plist"))
    agent.parent.mkdir(parents=True, exist_ok=True)
    if agent.exists():
        previous = backups / ("launchagent-before-" + uuid.uuid4().hex + ".plist")
        previous.write_bytes(agent.read_bytes())
        previous.chmod(0o600)
    temporary = agent.with_suffix(".plist.tmp-" + uuid.uuid4().hex)
    try:
        temporary.write_bytes(plistlib.dumps(make_plist(binary, data, backups)))
        temporary.chmod(0o600)
        os.replace(temporary, agent)
    finally:
        temporary.unlink(missing_ok=True)
    domain = "gui/" + str(os.getuid())
    subprocess.run(["launchctl", "bootout", domain + "/" + LABEL], capture_output=True, check=False)
    subprocess.run(["launchctl", "bootstrap", domain, str(agent)], check=True)
    print(json.dumps({"installed": str(agent), "dailyLocalTime": "03:00", "backupDirectory": str(backups)}))


def ensure_offline():
    """Refuse directory replacement while Web or MCP processes could retain old native handles or restart writes."""
    lines = subprocess.check_output(["ps", "-axo", "pid=,command="], text=True).splitlines()
    for line in lines:
        _, _, command = line.strip().partition(" ")
        if "AgMemory.Web" in command or "AgMemory.McpServer" in command:
            raise RuntimeError("Quit AgMemory and disconnect its MCP clients before activating a restore")


def activate_restore(data, prepared, rollback):
    """Swap verified directories with immediate rollback on rename failure; keep prior data permanently outside retention."""
    if not prepared.is_dir() or not (prepared / "memory-graph.lancedb").is_dir():
        raise ValueError("Prepared restore does not contain memory data")
    if rollback.exists():
        raise ValueError("Rollback destination already exists")
    existed = data.exists()
    if existed:
        data.rename(rollback)
    try:
        prepared.rename(data)
    except BaseException:
        if existed:
            rollback.rename(data)
        raise


def restore(binary, data, backups, archive):
    """Validate and stage a ZIP, stop the scheduled job, then replace only offline data and retain the prior directory."""
    ensure_offline()
    recovery = checked_path(backups.parent / "recovery")
    recovery.mkdir(mode=0o700, parents=True, exist_ok=True)
    prepared = recovery / ("prepared-" + uuid.uuid4().hex)
    subprocess.run([str(binary), "--restore-backup", str(archive), "--restore-to", str(prepared)], check=True)
    domain = "gui/" + str(os.getuid())
    service = domain + "/" + LABEL
    loaded = subprocess.run(["launchctl", "print", service], capture_output=True).returncode == 0
    if loaded:
        subprocess.run(["launchctl", "bootout", service], check=True)
    try:
        ensure_offline()
        stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
        rollback = recovery / ("before-restore-" + stamp + "-" + uuid.uuid4().hex[:8])
        activate_restore(data, prepared, rollback)
        print(json.dumps({"restored": str(data), "previousData": str(rollback), "restartClients": True}))
    finally:
        if prepared.exists():
            shutil.rmtree(prepared)
        if loaded:
            agent = Path.home() / "Library/LaunchAgents" / (LABEL + ".plist")
            subprocess.run(["launchctl", "bootstrap", domain, str(agent)], check=True)


def main():
    """Resolve trusted local paths and dispatch the requested scheduler or offline restore operation."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["install", "status", "restore"])
    parser.add_argument("archive", nargs="?")
    parser.add_argument("--app")
    parser.add_argument("--data-dir")
    parser.add_argument("--backup-dir")
    args = parser.parse_args()
    installed = read_installation("agmemory", DEFAULT_DATA, DEFAULT_BACKUPS)
    app = args.app or installed.application_path or "/Applications/AgMemory.app"
    data = checked_path(args.data_dir or os.environ.get("AGMEMORY_DATA_DIR") or installed.data_directory)
    selected_backup = installed.backup_directory if data == installed.data_directory else data.parent / "AgMemory-Backups/automatic"
    backups = checked_path(args.backup_dir or os.environ.get("AGMEMORY_BACKUP_DIR") or selected_backup)
    binary = checked_path(Path(app) / "Contents/MacOS/AgMemory.Web")
    if data == backups or data in backups.parents or backups in data.parents:
        raise ValueError("Data and backup roots must be separate")
    if args.command == "install":
        if not binary.is_file():
            raise ValueError("Installed AgMemory executable was not found")
        install(binary, data, backups)
    elif args.command == "status":
        result = subprocess.run(["launchctl", "print", "gui/" + str(os.getuid()) + "/" + LABEL], check=False)
        return result.returncode
    else:
        if not args.archive:
            parser.error("restore requires a ZIP archive")
        restore(binary, data, backups, checked_path(args.archive))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
