// :protocol —— 纯 Kotlin/JVM 模块：发现协议 + 加密信封。
//
// 刻意不引入任何 Android 依赖，和 :vban 一个道理：这两块都是"字节层"的东西，
// 能不能跟电脑端对上，必须能在电脑上用普通单元测试跑出来，而不是非得拿手机试。
//
// 为什么发现和安全放同一个模块：它们都是"要跟 C# 侧逐字节对齐的协议层"，
// 而且手机端要用的时候是一起用的（先找到电脑，再拿密钥连上去）。
// 拆成两个模块只是多一份 gradle 配置，没有任何收益。

plugins {
    id("org.jetbrains.kotlin.jvm")
}

kotlin {
    jvmToolchain(17)
}

dependencies {
    testImplementation("junit:junit:4.13.2")
}

tasks.withType<Test> {
    testLogging {
        events("passed", "failed", "skipped")
        showStandardStreams = false
    }
}
