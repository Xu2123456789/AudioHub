// :vban —— 纯 Kotlin/JVM 模块，只负责把 PCM 打成 VBAN 报文。
//
// 刻意不引入任何 Android 依赖：这样协议部分可以在电脑上直接跑单元测试，
// 不需要手机、不需要模拟器。手机端"发出去的包对不对"这件事，由此变成可自证的。

plugins {
    id("org.jetbrains.kotlin.jvm")
}

kotlin {
    jvmToolchain(17)
}

dependencies {
    // 与 PC 端 AudioHub.Tests 里的断言思路保持一致：先验证协议字节，再上真机。
    testImplementation("junit:junit:4.13.2")
}

tasks.withType<Test> {
    testLogging {
        events("passed", "failed", "skipped")
        showStandardStreams = false
    }
}
