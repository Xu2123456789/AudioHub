// :app —— 安卓发送端。
//
// 职责：把手机正在播放的声音（系统级回放捕获）打成 VBAN 报文，用 UDP 发给电脑上的 AudioHub。
//
// 三个取舍：
// 1. 界面仍用框架自带的 Activity 与基础控件手搭，不引 AppCompat/Material —— 只剩
//    "扫码页需要 CameraX"这一处引了 AndroidX，理由见下。
// 2. 协议编码不放在这里，而是放在 :vban 与 :protocol 模块。这样"发出去的包对不对"
//    可以在电脑上用普通 JVM 单元测试验证，不需要手机。
// 3. 扫码用 CameraX（取景/对焦）+ ZXing（解码）。早先刻意零 AndroidX、改用
//    "调系统相机拍一张再解码"，代价是要按快门、识别要等拍照完成。改用 CameraX 的原因
//    不是"Android 上最好用"，而是**这一层代码最少**：以后每个平台都要照着重写一遍，
//    手写 Camera2 的两百多行（旋转/对焦/各家 ROM 差异）会在每个平台重复一次。
//    可移植的部分（二维码文本格式）不在这一层，见 :protocol 的 ConnectionQr。
//    注意：CameraX 只出现在 app 模块，不许渗进 :protocol / :vban，
//    由 scripts/check-layer-purity.py 把关。

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
        // 2.1.0 = 短码 / 二维码手动连接。版本号只影响应用内更新的比较，必须单调递增。
        // 2.2.0 = 扫码改为实时预览（CameraX），并修掉"扫码/短码会把电脑原话改写成明文"。
        // 2.3.0 = 界面重做成卡片式（整页可滚、长提示不再被截断）+ 手机端每次开始前单播确认电脑身份与加密模式。
        // 2.3.1 = 顶部改成自己的抬头（去掉系统 ActionBar）+ 换电脑时清掉旧密钥 + 发送中复查电脑加密设置。
        // 2.3.2 = 发布前静态检查收尾：补 <queries>（否则拍照扫码在 Android 11+ 直接不可用）、
        //         密钥不随备份/换机迁出、安装未知来源的跳转加降级、lint 归零。
        // 2.3.3 = 排查"打开即白屏闪退"：把 2.3.2 加在 adaptive-icon 里的 <monochrome>
        //         和 mipmap-anydpi 目录改名一起撤回（恢复 -v26）。见 docs 里的排查记录。
        // 2.3.4 = 继续二分：2.3.1 原样 + 只保留 <queries>，撤掉 dataExtractionRules
        //         与 activity label 的改动。用来判定崩溃是不是清单资源引起的。
        // 2.3.5 = 修掉真正的崩溃：window.insetsController 在 setContentView 之前访问，
        //         PhoneWindow.getInsetsController() 内部解引用 mDecor（还 null）→ NPE。
        versionCode = 10
        versionName = "2.3.5"
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
    // 单元测试同理。扫码识别那部分刻意不碰 Android API，所以能在电脑上直接跑。
    sourceSets["test"].java.srcDirs("src/test/kotlin")

    lint {
        // 现在是 0 Error / 0 Warning，所以要求 lint 一旦出现 Error 就拦住构建。
        // 之前这里写的是 false（"图标之类的警告别拦我"），但那个开关同时把
        // 真错误也放过去了 —— QueryPermissionsNeeded 那种会直接打死拍照扫码的问题
        // 就是这么漏到实机上的。警告不拦，错误必须拦。
        abortOnError = true
        warningsAsErrors = false
    }
}

dependencies {
    // VBAN 报文编码器，纯 Kotlin/JVM，可被单元测试直接覆盖。
    implementation(project(":vban"))

    // 发现协议（找电脑）+ 加密信封（发加密音频），同样是纯 Kotlin/JVM。
    implementation(project(":protocol"))

    // 二维码解码只用 ZXing 的纯 Java 核心。
    // 不用 zxing-android-embedded：它自带一套取景界面，样式改不动，而且它会把 UI 和
    // 解码绑在一起——我们只需要"给我一帧亮度数据，还我一个二维码文本"，那一层自己写
    // 反而更薄（见 scan/ZxingQrScanner.kt）。
    implementation("com.google.zxing:core:3.5.3")

    // 实时扫码取景。CameraX 的价值在于它替我们承担旋转、对焦、预览与各家 ROM 差异，
    // 这些正是手写 Camera2 最容易出错、又跟协议毫无关系的部分。
    // activity 只为拿到 ComponentActivity（CameraX 需要 LifecycleOwner），
    // 它不要求 AppCompat 主题，所以现有 Theme.DeviceDefault 主题不用改。
    implementation("androidx.activity:activity:1.8.2")
    implementation("androidx.camera:camera-core:1.3.4")
    implementation("androidx.camera:camera-camera2:1.3.4")
    implementation("androidx.camera:camera-lifecycle:1.3.4")
    // camera-view 只为了 PreviewView。实测把它带进来的 camera-video 排除掉只省 91 字节，
    // 说明体积增加主要来自 AndroidX 基础库的类与资源，不是某一个能单独摘掉的模块。
    implementation("androidx.camera:camera-view:1.3.4")

    // 扫码识别可以完全在电脑上验：把二维码编码成灰度帧，再让扫描器认回来。
    // 这样"解码是否正确"不必等手机、不必拿相机对着屏幕，跑一条命令就有结论。
    testImplementation("junit:junit:4.13.2")
}
