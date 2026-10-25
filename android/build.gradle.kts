// 根构建脚本：只声明插件版本，不应用到根项目。
//
// 版本组合说明（改动需谨慎）：
//   Gradle 8.7  +  AGP 8.5.2  +  Kotlin 1.9.24  +  JDK 17
// AGP 8.5 要求 Gradle 8.7 或更高，且必须用 JDK 17 运行。三者是配套的，
// 单独升一个会报错，不要随手改版本号。

plugins {
    id("com.android.application") version "8.5.2" apply false
    id("org.jetbrains.kotlin.android") version "1.9.24" apply false
    id("org.jetbrains.kotlin.jvm") version "1.9.24" apply false
}
