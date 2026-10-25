// :simulator —— 在电脑上冒充手机发包的开发工具。
//
// 存在的理由：手机接进来之前，"电脑这一侧的接收路径到底通不通"没法验证。
// 这个工具用**和手机端完全相同**的 VbanUdpSender 和 VbanPacketWriter 发包，
// 所以它能证明的东西不止是"网络通"，而是"手机将来发出来的字节，电脑认得"。
//
// 它不是应用的一部分，只是开发期的一次性验证工具。

plugins {
    id("org.jetbrains.kotlin.jvm")
    application
}

kotlin {
    jvmToolchain(17)
}

dependencies {
    // 关键点：复用手机端那份编码与发送代码，不另写一份。
    implementation(project(":vban"))

    // 加密信封。加了它，这个工具才能冒充"一部已经配对过的手机"，
    // 于是"手机封的包，电脑解不开"这种跨端问题在电脑上就能复现和排查。
    implementation(project(":protocol"))
}

application {
    mainClass.set("com.audiohub.simulator.SimulatorKt")
}
