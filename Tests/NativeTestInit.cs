#if SMOD_NATIVE_TEST_HOST
global using Microsoft.VisualStudio.TestTools.UnitTesting;
[assembly: DoNotParallelize]
[TestClass]
public sealed class NativeTestInit
{
 static Sandbox.TestAppSystem app;
 [AssemblyInitialize] public static void Start(TestContext context) { app=new();app.Init(); }
 [AssemblyCleanup] public static void Stop() => app?.Shutdown();
}
#endif
