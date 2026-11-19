#!/usr/bin/python3
import argparse
import socket
import struct
import sys


def read_exact(connection, length):
    data = bytearray()
    while len(data) < length:
        part = connection.recv(length - len(data))
        if not part:
            raise ValueError("NBD server closed the connection during negotiation")
        data.extend(part)
    return data


def export_size(path):
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
        connection.settimeout(30)
        connection.connect(path)
        magic, options, flags = struct.unpack(">QQH", read_exact(connection, 18))
        if magic != 0x4E42444D41474943 or options != 0x49484156454F5054 or not flags & 1:
            raise ValueError("NBD server does not support fixed newstyle negotiation")
        connection.sendall(struct.pack(">I", 1 | (flags & 2)))
        connection.sendall(struct.pack(">QII", options, 1, 0))
        size, _ = struct.unpack(">QH", read_exact(connection, 10))
        return size


def main():
    parser = argparse.ArgumentParser(description="Check an NBD export before padding.")
    parser.add_argument("socket")
    parser.add_argument("maximum", type=int)
    args = parser.parse_args()
    try:
        size = export_size(args.socket)
        if size == 0 or size % 512 or size > args.maximum:
            raise ValueError(f"source size {size} is invalid or exceeds {args.maximum} bytes")
    except (OSError, ValueError) as error:
        print(f"Cannot use NBD export: {error}", file=sys.stderr)
        return 1
    print(size)
    return 0


if __name__ == "__main__":
    sys.exit(main())
