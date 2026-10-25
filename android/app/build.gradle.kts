// :app —— 安卓发送端。
//
// 职责：把手机正在播放的声音（系统级回放捕获）打成 VBAN 报文，用 UDP 发给电脑上的 AudioHub。
//
// 两个刻意的取舍：
// 1. 不用 AndroidX。界面全部用框架自带的 android.app.Activity 和基础控件手搭，
//    依赖最少、构建最快，也不会因为新版库要求更高 compileSdk 而突然构建不过。
// 2. 协议编码不放在这里，而是放在 :vban 模块。这样"发出去的包对不对"可以在电脑上
//    用普通 JVM 单元测试验证，不需要手机。

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.audiohub.sender"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.audiohub.sender"
        // 回放捕获（AudioPlaybackCapture）从 Android 10（API 29）才有；
        // 取 30 是因为 Android 11 起 MediaProjection 的前台服务要求才稳定下来。
        minSdk = 30
        targetSdk = 34
        // 1.0.0 = 第一个"真机上能正常听"的版本：电脑自激回授已修、本机缓冲已修、
        // 手机到电脑的链路在真机（Redmi K80 走 WiFi）上跑通。详见根目录 CHANGELOG.md。
        versionCode = 1
        versionName = "1.0.0"
    }

    buildTypes {
        // 自用，不做混淆；debug 签名直接装。
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    // Kotlin 源文件放在 src/main/kotlin，这里显式登记一次，避免因插件版本差异漏扫。
    sourceSets["main"].java.srcDirs("src/main/kotlin")

    lint {
        // 少了启动图标之类的只是警告，不要让它们把构建拦下来。
        abortOnError = false
    }
}

dependencies {
    // VBAN 报文编码器，纯 Kotlin/JVM，可被单元测试直接覆盖。
    implementation(project(":vban"))

    // 发现协议（找电脑）+ 加密信封（发加密音频），同样是纯 Kotlin/JVM。
    implementation(project(":protocol"))
}
