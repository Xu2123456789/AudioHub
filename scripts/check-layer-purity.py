#!/usr/bin/env python3
"""共享层禁入检查：协议层/core 里不许出现平台专属符号。

为什么需要这条检查：这个项目的核心原则之一是跨平台——一台设备混音，其他设备传音，
安卓只是当前用来验证的那个平台。协议字节、二维码文本、密钥派生这些东西必须
"新平台不看源码也能实现对"，所以它们不许依赖任何一个平台的 API。

一旦有人往协议层里塞一行 android.util.Log 或者 System.Windows.MessageBox，
编译不会报错、测试也不会变红，但那个平台之外的实现就再也对不上了。
这类破坏是静默的，所以必须有一条命令能当场把它抓出来。

用法：
    python scripts/check-layer-purity.py           # 检查，有问题退出码 1
    python scripts/check-layer-purity.py --list    # 打印每一层扫了哪些文件

退出码：
    0  干净
    1  发现违例
    2  一个文件都没扫到（路径写错了 / 目录改名了）—— 必须算失败，
       否则"扫了 0 个文件"会安静地通过，这条检查就变成了摆设。
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

# 每一层：扫哪里、只认什么后缀、禁什么。
#
# 白名单是显式的且必须理由充分——误报一次，这条检查就会被绕过：
#   javax.crypto 是 JVM 标准库，不是 Android；
#   java.util.UUID / java.net 同理，任何 JVM 平台都有；
#   System.Net.Sockets / System.Security.Cryptography 在 .NET 各平台行为一致。
LAYERS = [
    {
        "name": "协议层 (Kotlin, android/protocol)",
        "root": "android/protocol/src/main",
        "suffix": ".kt",
        "banned": [
            (r"\bandroid\.", "Android 平台 API（协议层要能被非 Android 平台照抄）"),
            (r"\bandroidx\.", "AndroidX"),
            (r"\bcom\.audiohub\.sender\b", "应用层包：协议层不该反过来依赖界面"),
            (r"\bkotlinx\.android\b", "Android 专属 Kotlin 扩展"),
        ],
    },
    {
        "name": "内核层 (C#, src/AudioHub.Core)",
        "root": "src/AudioHub.Core",
        "suffix": ".cs",
        "banned": [
            (r"\bSystem\.Windows\b", "WPF/WinForms API"),
            (r"\bSystem\.Drawing\b", "GDI+（Windows 专属）"),
            (r"\bMicrosoft\.Win32\b", "Win32 注册表/互操作"),
            (r"\bNAudio\b", "Windows 音频库"),
        ],
    },
]


def strip_comments(text: str) -> str:
    """去掉注释再扫，避免"不碰 Android API"这种说明性文字被判成违例。

    只削到行尾、不处理字符串转义。这样最多漏判（把字符串里当注释切掉），
    不会误判——而这条检查最怕的是误报，误报会让人直接绕过它。
    `://` 前面的 `//` 不当注释，因为二维码内容 `audiohub://c/1?...` 就在协议层里。
    """
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    lines = []
    for line in text.splitlines():
        cut = None
        for match in re.finditer(r"//", line):
            if match.start() > 0 and line[match.start() - 1] == ":":
                continue
            cut = match.start()
            break
        lines.append(line if cut is None else line[:cut])
    return "\n".join(lines)


def scan_layer(layer: dict) -> tuple[list[str], int]:
    root = REPO_ROOT / layer["root"]
    if not root.exists():
        return ([f"[配置错误] 目录不存在：{root}"], 0)

    violations: list[str] = []
    scanned = 0

    for path in sorted(root.rglob("*" + layer["suffix"])):
        if not path.is_file():
            continue

        scanned += 1
        raw = path.read_text(encoding="utf-8", errors="replace")
        body = strip_comments(raw)
        # 行号按去注释后的文本算，与原文一致：去注释不增删行。
        for number, line in enumerate(body.splitlines(), start=1):
            for pattern, why in layer["banned"]:
                if re.search(pattern, line):
                    rel = path.relative_to(REPO_ROOT).as_posix()
                    violations.append(
                        f"{rel}:{number}: 出现 {why}\n    {line.strip()}"
                    )

    return (violations, scanned)


def main() -> int:
    parser = argparse.ArgumentParser(description="共享层禁入检查")
    parser.add_argument("--list", action="store_true", help="打印每层扫了哪些文件")
    args = parser.parse_args()

    total_scanned = 0
    total_violations = 0
    print("=== 共享层禁入检查 ===")
    print("（协议字节 / 二维码文本 / 密钥派生必须与平台无关，否则跨平台时只能重写）")
    print()

    for layer in LAYERS:
        violations, scanned = scan_layer(layer)
        total_scanned += scanned
        total_violations += len(violations)

        status = "OK" if not violations else f"{len(violations)} 处违例"
        print(f"{layer['name']}：扫 {scanned} 个文件 → {status}")

        if args.list and scanned:
            root = REPO_ROOT / layer["root"]
            for path in sorted(root.rglob("*" + layer["suffix"])):
                if path.is_file():
                    print(f"    {path.relative_to(REPO_ROOT).as_posix()}")

        for item in violations:
            print(f"  ✗ {item}")
        print()

    if total_scanned == 0:
        print("FAIL：一个文件都没扫到。多半是目录改名了——这条检查如果扫 0 个文件就通过，")
        print("      等于没有。请先修 LAYERS 里的路径。")
        return 2

    if total_violations:
        print(f"FAIL：共 {total_violations} 处平台符号出现在共享层。")
        print("      要么把代码挪到平台层，要么把这个符号加进白名单并说明理由（在 LAYERS 注释里）。")
        return 1

    print(f"PASS：{total_scanned} 个文件里没有平台专属符号。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
