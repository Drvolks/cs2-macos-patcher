#!/usr/bin/env python3
"""
Re-encrypt Cities Skylines 2 DLC manifests (.ntl files) with a deterministic key.

Companion to Fix 25 (HashHelperComputeHashPatcher) which changes the AES-128 key
derivation from "hash all file paths in the directory" to a simple
`xxHash3(dlcName + ".ntl")` using the C# string hashing semantics.

The C# `HashHelper.Update(state, string)` pins the .NET string (UTF-16 LE) and
calls `StreamingState.Update(ptr, str.Length)`. `str.Length` returns the
character count, so only the first `N` bytes of the UTF-16 representation are
hashed (NOT the full 2×N bytes).

This script replicates that: encode the DLC name as UTF-16 LE, take the first
`len(dlc_name + ".ntl")` bytes, and xxHash3 them.

Usage:
    python3 ntl-reencrypt.py [PATH_TO_CONTENT_DIR]
"""

import os
import sys
import json
import secrets
from pathlib import Path

try:
    import xxhash
except ImportError:
    sys.exit("pip3 install --break-system-packages xxhash")

try:
    from Crypto.Cipher import AES
    from Crypto.Util.Padding import pad, unpad
except ImportError:
    sys.exit("pip3 install --break-system-packages pycryptodome")


def compute_key(dlc_name: str) -> bytes:
    """Compute AES key matching C# HashHelper.ComputeHash.

    C# passes the string (UTF-16 LE in memory) to xxHash3.StreamingState.Update
    with byteCount = str.Length (number of characters, NOT number of bytes).
    So only the first `len(text)` bytes from the UTF-16 LE buffer are hashed.

    For "Game.ntl" (len=9), we hash the first 9 bytes of UTF-16 LE:
        47 00 61 00 6D 00 65 00 2E
    (the 9th byte is the first byte of the fifth character '.').
    """
    seed = f"{dlc_name}.ntl"
    utf16_bytes = seed.encode("utf-16-le")
    # first len(seed) bytes → matches C# get_Length() byte count
    bytes_to_hash = utf16_bytes[: len(seed)]
    return xxhash.xxh3_128(bytes_to_hash).digest()


def test_round_trip():
    """Verify encrypt → decrypt works with the correct C#-style key."""
    key = compute_key("Game")
    iv = secrets.token_bytes(16)
    plaintext = b'{"dlcId":0}'
    cipher = AES.new(key, AES.MODE_CBC, iv)
    ct = cipher.encrypt(pad(plaintext, 16))
    encrypted = iv + ct

    # Decrypt
    iv2 = encrypted[:16]
    ct2 = encrypted[16:]
    cipher2 = AES.new(key, AES.MODE_CBC, iv2)
    pt2 = cipher2.decrypt(ct2)
    pad_byte = pt2[-1]
    assert 1 <= pad_byte <= 16, f"Bad padding byte: {pad_byte}"
    assert all(b == pad_byte for b in pt2[-pad_byte:]), "Bad PKCS7 padding"
    pt2 = pt2[:-pad_byte]
    assert pt2 == plaintext, f"Round-trip failed: {pt2} != {plaintext}"
    print(f"  self-test passed (round-trip OK, key={key.hex()})")


def find_content_dir() -> str | None:
    bottle_root = os.path.expanduser(
        "~/Library/Application Support/CrossOver/Bottles")
    if not os.path.isdir(bottle_root):
        return None
    for bottle in os.listdir(bottle_root):
        p = os.path.join(bottle_root, bottle, "drive_c",
                         "Program Files (x86)/Steam/steamapps/common",
                         "Cities Skylines II/Cities2_Data/Content")
        if os.path.isdir(p):
            return p
    return None


def main() -> int:
    test_round_trip()
    print()

    if len(sys.argv) > 1:
        content_dir = os.path.expanduser(sys.argv[1])
    else:
        content_dir = find_content_dir()
        if content_dir is None:
            print("Could not auto-detect Cities2_Data/Content. Pass path.")
            return 1

    if not os.path.isdir(content_dir):
        print(f"Not a directory: {content_dir}")
        return 1

    ntl_files = []
    for root, dirs, files in os.walk(content_dir):
        for f in files:
            if f.endswith(".ntl"):
                ntl_files.append(Path(root) / f)

    if not ntl_files:
        print("No .ntl files found in", content_dir)
        return 0

    print(f"Found {len(ntl_files)} .ntl file(s)")
    print()

    dlc_defaults = {"Game": 0}

    count = 0
    for ntl_path in ntl_files:
        dlc_name = ntl_path.parent.name
        key = compute_key(dlc_name)

        dlc_id = dlc_defaults.get(dlc_name, 0)
        plaintext = json.dumps({"dlcId": dlc_id}).encode("utf-8")
        iv = secrets.token_bytes(16)
        cipher = AES.new(key, AES.MODE_CBC, iv)
        ct = cipher.encrypt(pad(plaintext, 16))
        encrypted = iv + ct

        original = ntl_path.read_bytes()
        if encrypted == original:
            print(f"  SKIP  {dlc_name}.ntl ({len(original)} bytes) — already re-encrypted")
            continue

        bak = ntl_path.with_suffix(".ntl.pre-fix25.bak")
        if not bak.exists():
            bak.write_bytes(original)

        ntl_path.write_bytes(encrypted)
        print(f"  OK    {dlc_name}.ntl ({len(original)}→{len(encrypted)} bytes) key={key.hex()}")
        count += 1

    print()
    print(f"Re-encrypted {count} .ntl file(s).")
    if count > 0:
        print("Backups at <DLC>/.ntl.pre-fix25.bak")
        print()
        print("Restart Cities: Skylines 2. The Content integrity check should now pass.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
