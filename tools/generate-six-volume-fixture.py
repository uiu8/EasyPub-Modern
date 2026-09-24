"""Deterministic 690-chapter fixture; independent of production recognition code."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "samples" / "六卷690章修复验收"
COUNTS = (100, 100, 130, 100, 130, 130)


def chapter(v, n):
    title = f"第{n}章 卷{v}的旅程{n}"
    body = [f"这一天，第{v}条旅途的第{n}段故事在晨光中展开。",
            f"人物留下了只属于卷{v}章{n}的记录，天长地久不是编号错误。",
            f"此段经历以卷{v}章{n}的约定结束，下一段故事仍在前方。"]
    return title, body


def build():
    complete, catalog, broken, truth = [], [], [], []
    for v, count in enumerate(COUNTS, 1):
        complete.append(f"第{v}卷 旅程{v}")
        catalog.append(f"# 第{v}卷 旅程{v}")
        for n in range(1, count + 1):
            title, body = chapter(v, n)
            catalog.append(title)
            complete.extend([title, *body, ""])
            truth.append(dict(id=f"v{v}-c{n}", volume=v, number=n, title=title,
                              body_sha256=hashlib.sha256("\n".join(body).encode()).hexdigest(),
                              missing_body=v == 4 and 50 <= n <= 60,
                              missing_heading=v == 4 and 30 <= n <= 40))

    instances = []

    def emit(v, n, duplicate=False):
        title, body = chapter(v, n)
        start = len(broken) + 1
        if not (v == 4 and 30 <= n <= 40):
            if v == 5 and 100 <= n <= 105:
                title = title[1:]
            elif v == 5 and 110 <= n <= 115:
                title = f"{n:03d}：卷{v}的旅程{n}"
            elif v == 5 and 120 <= n <= 125:
                digits = ("零", "一", "二", "三", "四", "五")
                # Explicit malformed numeral token; never modify ordinary body text.
                title = f"第一百二十{digits[n-120]}久章 卷{v}的旅程{n}"
            broken.append(title)
            if v == 3 and 70 <= n <= 80:
                broken.append(title)
        body_start = len(broken) + 1
        broken.extend([*body, ""])
        instances.append(dict(id=f"v{v}-c{n}", start_line=start,
                              body_start_line=body_start, body_end_line=body_start + len(body) - 1,
                              duplicate=duplicate))

    for v, count in enumerate(COUNTS, 1):
        for n in range(1, count + 1):
            if v == 2 and 20 <= n <= 40 or v == 4 and 50 <= n <= 60:
                continue
            if v == 3 and n == 20:
                for moved in range(20, 41):
                    emit(2, moved)
            emit(v, n)
            if v == 1 and 30 <= n <= 40:
                emit(v, n, duplicate=True)

    assert len(truth) == 690
    assert len(instances) == 690
    assert len({x["id"] for x in instances}) == 679
    assert sum(x["duplicate"] for x in instances) == 11
    by_id = {x["id"]: x for x in truth}
    for instance in instances:
        actual = "\n".join(broken[instance["body_start_line"]-1:instance["body_end_line"]])
        assert hashlib.sha256(actual.encode()).hexdigest() == by_id[instance["id"]]["body_sha256"]

    files = {"完整原稿.txt": "\n".join(complete) + "\n",
             "缺陷样书.txt": "\n".join(broken) + "\n",
             "参考目录.txt": "\n".join(catalog) + "\n"}
    manifest = dict(schema=1, chapters=truth, instances=instances,
                    hashes={name: hashlib.sha256(value.encode()).hexdigest() for name, value in files.items()})
    files["真值清单.json"] = json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
    ROOT.mkdir(parents=True, exist_ok=True)
    for name, content in files.items():
        target = ROOT / name
        data = content.encode("utf-8")
        if target.exists() and target.read_bytes() != data:
            raise RuntimeError(f"拒绝覆盖内容不同的样本：{target}")
        target.write_bytes(data)
    print(json.dumps(dict(expected=690, body_instances=690, distinct_bodies=679,
                          duplicate_instances=11, missing_bodies=11, hashes=manifest["hashes"]), ensure_ascii=False))


if __name__ == "__main__":
    build()
