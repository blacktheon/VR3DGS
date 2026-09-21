"""Small hand-authored PLYs, independent of the production parser/decoder."""

import struct
from pathlib import Path

FIELDS = ["x", "y", "z", "rot_0", "rot_1", "rot_2", "rot_3",
          "scale_0", "scale_1", "scale_2", "opacity", "f_dc_0", "f_dc_1", "f_dc_2"]


def rows():
    result = []
    for position, red, q in [((1, 2, 3), 0, 2), ((1, 2, 3), 1, 1),
                              ((3, 0, 5), -1, 1), ((-2, -4, 2), 0.5, 1)]:
        row = dict.fromkeys(FIELDS, 0.0)
        row.update(zip(("x", "y", "z"), position))
        row["rot_0"] = q
        row["f_dc_0"] = red
        result.append(row)
    return result


def write_ply(path: Path, records=None, fields=None):
    records = rows() if records is None else records
    fields = FIELDS if fields is None else fields
    header = ("ply\nformat binary_little_endian 1.0\ncomment independent fixture\n"
              f"element vertex {len(records)}\n"
              + "".join(f"property float {field}\n" for field in fields)
              + "end_header\n").encode("ascii")
    payload = b"".join(struct.pack("<" + "f" * len(fields), *(row[field] for field in fields))
                       for row in records)
    path.write_bytes(header + payload)
    return path, header, payload
