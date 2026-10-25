// AudioHub 手机端 App 的构建配置。
//
// 工具链目录约定（务必遵守）：
//   D:\app\Android\JDK-17         JDK 17，Gradle 运行时使用
//   D:\app\Android\Android-SDK    Android SDK（由 sdkmanager 安装的组件）
//   D:\app\Android\Gradle-8.7     Gradle 发行版
//   D:\app\Android\Gradle-Home    Gradle 缓存（GRADLE_USER_HOME），避免污染用户目录
//
// 构建命令见 android/README.md。脚本不假设固定位置，可以用 AUDIOHUB_TOOLCHAIN 覆盖根目录。

pluginManagement {
    repositories {
        // 国内镜像放在前面：直连 Google Maven 和 Gradle 插件门户在国内经常超时。
        // 镜像上缺某个版本时，Gradle 会自动往后面的官方源回落，所以官方源必须保留。
        maven("https://maven.aliyun.com/repository/gradle-plugin")
        maven("https://maven.aliyun.com/repository/google")
        maven("https://maven.aliyun.com/repository/public")

        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositories {
        maven("https://maven.aliyun.com/repository/google")
        maven("https://maven.aliyun.com/repository/public")

        google()
        mavenCentral()
    }
}

rootProject.name = "AudioHubSender"

// :vban —— 纯 Kotlin/JVM 库，只做 VBAN 报文的编码。
// 刻意不依赖 Android，这样协议部分可以在电脑上用普通单元测试跑，不需要手机或模拟器。
include(":vban")

// :protocol —— 纯 Kotlin/JVM：发现协议（找电脑）+ 加密信封（AES-256-GCM）。
// 和 :vban 一样不依赖 Android，因为这两块都是"字节层"，必须能在电脑上逐字节自证，
// 而不是等拿到真手机才知道对不对。
include(":protocol")

// :app —— Android 应用：录音捕获（MediaProjection）、UDP 发送、界面。
include(":app")

// :simulator —— 只在电脑上跑的开发工具，冒充手机发包。
// 它复用 :vban 里手机端同款的编码与发送代码，所以它能证明的不只是"网络通"，
// 而是"手机将来发出去的字节，电脑认得"。不需要手机在场。
include(":simulator")
