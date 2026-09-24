"""Independent single-defect baselines; no production recognition dependencies."""
import hashlib
import json
from pathlib import Path
from importlib.util import spec_from_file_location, module_from_spec

spec = spec_from_file_location("six", Path(__file__).with_name("generate-six-volume-fixture.py"))
six = module_from_spec(spec)
spec.loader.exec_module(six)
root = Path(__file__).resolve().parents[1] / "samples" / "章节独立场景"


def digest(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def build(case):
    full, catalog, broken, truth, instances = [], [], [], [], []
    for v, count in enumerate(six.COUNTS, 1):
        volume = f"第{v}卷 旅程{v}"
        full.append(volume)
        catalog.append(volume)
        for n in range(1, count + 1):
            title, body = six.chapter(v, n)
            # S9 uses an actual 九→久 typo, with neighboring numbering 118/120.
            if case == "S9" and v == 5 and n == 119:
                title = "第一百一十九章 卷5的旅程119"
            full.extend([title, *body, ""])
            catalog.append(title)
            truth.append(dict(id=f"v{v}-c{n}", volume=v, number=n, title=title,
                              body_sha256=digest("\n".join(body)),
                              missing_body=case == "S6" and v == 4 and 50 <= n <= 60,
                              missing_heading=case == "S5" and v == 4 and 30 <= n <= 40))

    def emit(v, n, duplicate=False):
        title, body = six.chapter(v, n)
        start = len(broken) + 1
        if case == "S7" and v == 5 and 100 <= n <= 105:
            title = title[1:]
        if case == "S8" and v == 5 and 110 <= n <= 115:
            title = f"{n:03d}：卷{v}的旅程{n}"
        if case == "S9" and v == 5 and n == 119:
            title = "第一百一十久章 卷5的旅程119"
        if not (case == "S5" and v == 4 and 30 <= n <= 40):
            broken.append(title)
            if case == "S4" and v == 3 and 70 <= n <= 80:
                broken.extend(["", title])
        body_start = len(broken) + 1
        broken.extend([*body, ""])
        instances.append(dict(id=f"v{v}-c{n}", start_line=start,
                              body_start_line=body_start, body_end_line=body_start + 2,
                              duplicate=duplicate))

    for v, count in enumerate(six.COUNTS, 1):
        if case != "S1":
            broken.append(f"第{v}卷 旅程{v}")
        for n in range(1, count + 1):
            if case == "S3" and v == 2 and 20 <= n <= 40:
                continue
            if case == "S6" and v == 4 and 50 <= n <= 60:
                continue
            if case == "S3" and v == 3 and n == 20:
                for moved in range(20, 41):
                    emit(2, moved)
            emit(v, n)
            if case == "S2" and v == 1 and 30 <= n <= 40:
                emit(v, n, True)

    by_id = {c["id"]: c for c in truth}
    assert len(truth) == 690
    assert len(instances) == (701 if case == "S2" else 679 if case == "S6" else 690)
    assert len({i["id"] for i in instances}) == (679 if case == "S6" else 690)
    for i in instances:
        assert digest("\n".join(broken[i["body_start_line"] - 1:i["body_end_line"]])) == by_id[i["id"]]["body_sha256"]
    if case == "S3":
        ids = [i["id"] for i in instances]
        pos = ids.index("v3-c19")
        assert ids[pos + 1:pos + 22] == [f"v2-c{n}" for n in range(20, 41)]
        assert ids[pos + 22] == "v3-c20"
    files = {"完整原稿.txt": "\n".join(full) + "\n",
             "缺陷样书.txt": "\n".join(broken) + "\n",
             "参考目录.txt": "\n".join(catalog) + "\n"}
    manifest = dict(schema=1, case=case, chapters=truth, instances=instances,
                    hashes={name: digest(text) for name, text in files.items()})
    files["真值清单.json"] = json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
    folder = root / case
    folder.mkdir(parents=True, exist_ok=True)
    for name, text in files.items():
        path = folder / name
        data = text.encode("utf-8")
        if path.exists() and path.read_bytes() != data:
            raise RuntimeError(f"拒绝覆盖不同样本：{path}")
        path.write_bytes(data)
    print(case, len(instances), "body instances verified")


if __name__ == "__main__":
    for scenario in ["S0", *[f"S{i}" for i in range(1, 10)]]:
        build(scenario)
