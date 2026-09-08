#pragma warning disable CS0436

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using LitJson;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using JsonWriter = LitJson.JsonWriter;

namespace KSWASM
{
    public class KSConvertCoreCommand
    {
        public delegate void PostProcess();

        private static PostProcess postProcessHandler;

        public static void RegisterPostProcessHandler(PostProcess postProcess)
        {
            postProcessHandler += postProcess;
        }

        public static void UnregisterPostProcessHandler(PostProcess postProcess)
        {
            postProcessHandler -= postProcess;
        }

        static KSConvertCoreCommand() { }

        public enum KSExportError
        {
            SUCCEED = 0,
            NODE_NOT_FOUND = 1,
            BUILD_WEBGL_FAILED = 2,
        }

        private static KSEditorScriptObject config;
        public static string webglDir = "webgl"; // 导出的webgl目录
        public static string miniGameDir = "minigame"; // 生成小游戏的目录
        public static string audioDir = "Assets"; // 音频资源目录
        public static string frameworkDir = "framework";
        public static string dataFileSize = string.Empty;
        public static string codeMd5 = string.Empty;
        public static string dataMd5 = string.Empty;
        private static string SDKFilePath = string.Empty;
        public static string defaultImgSrc =
            "Assets/KS-WASM-SDK-V2/Runtime/minigame-default/images/background.jpg";
        public static string FirstBundlePath = "";
        private static bool lastBrotliType = false;
        public static string workersDir = "workers/response";

        private static bool isSupportWasmSplit = false;

        public static KSExportError DoExport()
        {
            LifeCycleEvent.Init();

            config = UnityUtil.GetEditorConf(
                "kuaishou",
                "Assets/KS-WASM-SDK-V2/Editor/MiniGameConfig.asset"
            );

            LifeCycleEvent.Emit(LifeCycle.beforeExport);
            if (!CheckSDK())
            {
                Debug.LogError(
                    "若游戏曾使用旧版本快手SDK，需删除 Assets/KS-WASM-SDK 文件夹后再导入最新工具包。"
                );
                config = null;
                return KSExportError.BUILD_WEBGL_FAILED;
            }
            if (!CheckBuildTemplate())
            {
                Debug.LogError("因构建模板检查失败终止导出。");
                config = null;
                return KSExportError.BUILD_WEBGL_FAILED;
            }

            if (PlayerSettings.colorSpace == ColorSpace.Linear && !config.CompileOptions.Webgl2)
            {
                Debug.LogError(
                    "Linear color space 需使用Webgl2，终止导出。请在 Player Settings 的 Graphics APIs 中手动选择 WebGL 2."
                );
                config = null;
                return KSExportError.BUILD_WEBGL_FAILED;
            }

            UnityUtil.CheckBuildTarget();
            SDKFilePath = Path.Combine(
                UnityUtil.GetKsSDKRootPath(),
                "Runtime",
                "minigame-default",
                "unity-sdk",
                "index.js"
            );
            UnityUtil.InitExportPlayerSetting();
            UpdateGraphicAPI();
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();

            {
                var filePath = Path.Combine(
                    config.ProjectConf.DST,
                    miniGameDir,
                    "unity-namespace.js"
                );
                string content = string.Empty;
                if (File.Exists(filePath))
                {
                    content = File.ReadAllText(filePath, Encoding.UTF8);
                }
                Regex regex = new Regex("brotliMT\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                Match match = regex.Match(content);
                if (match.Success)
                {
                    lastBrotliType = match.Groups[1].Value == "true";
                }
            }

            if (config.ProjectConf.DST == string.Empty)
            {
                Debug.LogError("请先配置游戏导出路径");
                config = null;
                return KSExportError.BUILD_WEBGL_FAILED;
            }
            else
            {
                if (config.CompileOptions.DeleteStreamingAssets)
                {
                    UnityUtil.DelectDir(
                        Path.Combine(config.ProjectConf.DST, webglDir + "/StreamingAssets")
                    );
                }

                if (UnityUtil.PerformBuild(config) != 0)
                {
                    config = null;
                    return KSExportError.BUILD_WEBGL_FAILED;
                }

                if (
                    KSExtEnvDef.GETDEF("UNITY_2021_2_OR_NEWER")
                    && !config.CompileOptions.DevelopBuild
                )
                {
                    var symFile1 = "";
                    if (!UnityUtil.UseIL2CPP())
                    {
                        symFile1 = Path.Combine(
                            config.ProjectConf.DST,
                            webglDir,
                            "Code",
                            "wwwroot",
                            "_framework",
                            "dotnet.native.js.symbols"
                        );
                    }
                    else
                    {
                        var rootPath = Directory.GetParent(Application.dataPath).FullName;
                        if (KSExtEnvDef.GETDEF("UNITY_2021_2_5"))
                        {
                            symFile1 = Path.Combine(
                                rootPath,
                                "Library",
                                "Bee",
                                "artifacts",
                                "WebGL",
                                "build",
                                "debug_WebGL_wasm",
                                "build.js.symbols"
                            );
                            if (!File.Exists(symFile1))
                            {
                                symFile1 = Path.Combine(
                                    rootPath,
                                    "Library",
                                    "Bee",
                                    "artifacts",
                                    "WeixinMiniGame",
                                    "build",
                                    "debug_WebGL_wasm",
                                    "build.js.symbols"
                                );
                            }
                        }
                        else
                        {
                            string webglDir =
                                KSExtEnvDef.GETDEF("MINIGAME_SUBPLATFORM_KUAISHOU")
                                || KSExtEnvDef.GETDEF("UNITY_MINIGAME")
                                || KSExtEnvDef.GETDEF("WEIXINMINIGAME")
                                    ? "WeixinMiniGame"
                                    : "WebGL";
                            symFile1 = Path.Combine(
                                rootPath,
                                "Library",
                                "Bee",
                                "artifacts",
                                webglDir,
                                "build",
                                "debug_WebGL_wasm",
                                "build.js.symbols"
                            );
                        }
                    }
                    UnityUtil.preprocessSymbols(symFile1, GetWebGLSymbolPath());
                }

                ConvertCode();
                if (!UnityUtil.UseIL2CPP())
                {
                    ConvertDotnetCode();
                }
                if (!finishExport())
                {
                    config = null;
                    return KSExportError.BUILD_WEBGL_FAILED;
                }
            }

            config = null;
            return KSExportError.SUCCEED;
        }

        public static string RemoveFunctionsWithPrefix(string input, string prefix)
        {
            StringBuilder output = new StringBuilder();

            int braceCount = 0;
            int lastIndex = 0;
            int index = input.IndexOf("function " + prefix);

            while (index != -1)
            {
                output.Append(input, lastIndex, index - lastIndex);
                lastIndex = index;

                while (input[lastIndex] != '{')
                {
                    lastIndex++;
                }

                braceCount = 1;
                ++lastIndex;

                while (braceCount > 0)
                {
                    if (input[lastIndex] == '{')
                    {
                        ++braceCount;
                    }
                    else if (input[lastIndex] == '}')
                    {
                        --braceCount;
                    }
                    ++lastIndex;
                }

                index = input.IndexOf("function " + prefix, lastIndex);
            }

            output.Append(input, lastIndex, input.Length - lastIndex);

            return output.ToString();
        }

        private static bool CheckBuildTemplate()
        {
            string[] res = BuildTemplate.DetectTemplateConflicts(
                Path.Combine(UnityUtil.GetKsSDKRootPath(), "Runtime", "minigame-default"),
                Path.Combine(Application.dataPath, "KS-WASM-SDK-V2", "Editor", "template"),
                new string[] { @"\.(js|ts|json)$" }
            );
            if (res.Length != 0)
            {
                Debug.LogError(
                    "系统发现自定义构建模板中存在以下文件对应的基础模板已被更新，为确保游戏导出正常工作请自行解决可能存在的冲突："
                );
                for (int i = 0; i < res.Length; i++)
                {
                    Debug.LogError($"自定义模板文件 [{i}]: [ {res[i]} ]");
                }
                return false;
            }
            return true;
        }

        private static void ConvertDotnetCode()
        {
            CompressAssemblyBrotli();
            ConvertDotnetRuntimeCode();
            ConvertDotnetFrameworkCode();
        }

        private static void ConvertDotnetRuntimeCode()
        {
            var runtimePath = GetWeixinMiniGameFilePath("jsModuleRuntime")[0];
            var dotnetJs = File.ReadAllText(runtimePath, Encoding.UTF8);

            Rule[] rules =
            {
                new Rule()
                {
                    old = "await *WebAssembly\\.instantiate\\(\\w*,",
                    newStr = $"await WebAssembly.instantiate(Module[\"wasmPath\"],",
                },
                new Rule()
                {
                    old = "['\"]Expected methodFullName if trace is instrumented['\"]\\);?",
                    newStr = "'Expected methodFullName if trace is instrumented'); return;",
                },
            };
            foreach (var rule in rules)
            {
                if (ShowMatchFailedWarning(dotnetJs, rule.old, "runtime") == false)
                {
                    dotnetJs = Regex.Replace(dotnetJs, rule.old, rule.newStr);
                }
            }

            File.WriteAllText(
                Path.Combine(
                    config.ProjectConf.DST,
                    miniGameDir,
                    frameworkDir,
                    Path.GetFileName(runtimePath)
                ),
                dotnetJs,
                new UTF8Encoding(false)
            );
        }

        private static void CompressAssemblyBrotli()
        {
            GetWeixinMiniGameFilePath("assembly")
                .ToList()
                .ForEach(assembly => UnityUtil.compressBrotli(assembly, assembly + ".br", "8"));
        }

        private static void ConvertDotnetFrameworkCode()
        {
            var target = "webgl.wasm.framework.unityweb.js";
            var dotnetJsPath = Path.Combine(
                config.ProjectConf.DST,
                webglDir,
                "Code",
                "wwwroot",
                "_framework",
                "dotnet.js"
            );
            var dotnetJs = File.ReadAllText(dotnetJsPath, Encoding.UTF8);
            // todo: handle dotnet js
            foreach (
                var rule in ReplaceRules.DoenetRules(
                    new string[]
                    {
                        frameworkDir,
                        Path.GetFileName(GetWeixinMiniGameFilePath("jsModuleRuntime")[0]),
                        Path.GetFileName(GetWeixinMiniGameFilePath("jsModuleNative")[0]),
                    }
                )
            )
            {
                if (ShowMatchFailedWarning(dotnetJs, rule.old, "dotnet") == false)
                {
                    dotnetJs = Regex.Replace(dotnetJs, rule.old, rule.newStr);
                }
            }
            File.WriteAllText(
                Path.Combine(config.ProjectConf.DST, miniGameDir, frameworkDir, target),
                ReplaceRules.DotnetHeader + dotnetJs + ReplaceRules.DotnetFooter,
                new UTF8Encoding(false)
            );
        }

        private static void ConvertCode()
        {
            Debug.LogFormat(
                "[Converter] Starting to adapt framework. Dst: " + config.ProjectConf.DST
            );

            UnityUtil.DelectDir(Path.Combine(config.ProjectConf.DST, miniGameDir));
            string text = String.Empty;
            var target = "webgl.wasm.framework.unityweb.js";
            if (KSExtEnvDef.GETDEF("UNITY_2020_1_OR_NEWER"))
            {
                if (UnityUtil.UseIL2CPP())
                {
                    text = File.ReadAllText(
                        Path.Combine(
                            config.ProjectConf.DST,
                            webglDir,
                            "Build",
                            "webgl.framework.js"
                        ),
                        Encoding.UTF8
                    );
                }
                else
                {
                    var frameworkPath = GetWeixinMiniGameFilePath("jsModuleNative")[0];
                    target = Path.GetFileName(frameworkPath);
                    text = File.ReadAllText(frameworkPath, Encoding.UTF8);
                }
            }
            else
            {
                text = File.ReadAllText(
                    Path.Combine(
                        config.ProjectConf.DST,
                        webglDir,
                        "Build",
                        "webgl.wasm.framework.unityweb"
                    ),
                    Encoding.UTF8
                );
            }
            int i;
            for (i = 0; i < ReplaceRules.rules.Length; i++)
            {
                var current = i + 1;
                var total = ReplaceRules.rules.Length;
                EditorUtility.DisplayProgressBar(
                    $"Converting...，{current}/{total}",
                    "Replace holder...",
                    current * 1.0f / total
                );
                var rule = ReplaceRules.rules[i];
                // text = Regex.Replace(text, rule.old, rule.newStr);
                if (ShowMatchFailedWarning(text, rule.old, "KSReplaceRules") == false)
                {
                    text = Regex.Replace(text, rule.old, rule.newStr);
                }
            }

            EditorUtility.ClearProgressBar();

            if (
                KSExtEnvDef.GETDEF("TUANJIE_2022_3_OR_NEWER")
                && UnityUtil.UseIL2CPP()
                && config.CompileOptions.DevelopBuild
                && config.CompileOptions.ScriptDebugging
            )
            {
                var debuggerRules = ReplaceRules.DebuggerRules(target);
                for (i = 0; i < debuggerRules.Length; i++)
                {
                    var current = i + 1;
                    var total = debuggerRules.Length;
                    EditorUtility.DisplayProgressBar(
                        $"Converting Debugger rules...，{current}/{total}",
                        "Replace holder...",
                        current * 1.0f / total
                    );
                    var rule = debuggerRules[i];
                    if (ShowMatchFailedWarning(text, rule.old, "KSReplaceRules") == false)
                    {
                        text = Regex.Replace(text, rule.old, rule.newStr);
                    }
                }
                EditorUtility.ClearProgressBar();
                var debuggerWorkerJs = File.ReadAllText(
                    Path.Combine(config.ProjectConf.DST, webglDir, "Build", "webgl.worker.js"),
                    Encoding.UTF8
                );
                var debuggerWebSocketWrokerJs = File.ReadAllText(
                    Path.Combine(
                        config.ProjectConf.DST,
                        webglDir,
                        "Build",
                        "debugger-websocket-worker.js"
                    ),
                    Encoding.UTF8
                );
                var targetDir = Path.Combine(config.ProjectConf.DST, miniGameDir, workersDir);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }
                var pattern =
                    "socket[\\s]*.[\\s]*socket[\\s]*.[\\s]*onmessage[\\s]* =.*{([\\s\\S]+?)};";

                debuggerWebSocketWrokerJs = Regex.Replace(
                    debuggerWebSocketWrokerJs,
                    pattern,
                    @"
  socket.socket.onmessage = function (e) {
        var array = new Uint8Array(e.data);
        if (array.length == 1 && array[0] == 255) {return;}
        socket.recv_queue.push(array);
    };
"
                );
                var workerHeader = "var *Module";
                var websocketHeader = "var *DebuggerSockets";
                debuggerWorkerJs = Regex.Replace(
                    debuggerWorkerJs,
                    workerHeader,
                    "importScripts(\"workers/response/ks-adapter-worker.js\");var Module"
                );
                debuggerWebSocketWrokerJs = Regex.Replace(
                    debuggerWebSocketWrokerJs,
                    websocketHeader,
                    "importScripts(\"workers/response/ks-adapter-worker.js\");var DebuggerSockets"
                );
                File.WriteAllText(
                    Path.Combine(targetDir, "debugger-websocket-worker.js"),
                    debuggerWebSocketWrokerJs,
                    new UTF8Encoding(false)
                );
                File.WriteAllText(
                    Path.Combine(targetDir, "debugger.worker.js"),
                    debuggerWorkerJs,
                    new UTF8Encoding(false)
                );
            }
            else
            {
                string[] prefixs =
                {
                    "_JS_Video_",
                    //"jsVideo",
                    "_JS_Sound_",
                    "jsAudio",
                    "_JS_MobileKeyboard_",
                    "_JS_MobileKeybard_",
                };
                foreach (var prefix in prefixs)
                {
                    text = RemoveFunctionsWithPrefix(text, prefix);
                }
            }

            if (UnityUtil.ExceptionSupportIsNone())
            {
                Rule[] rules =
                {
                    new Rule()
                    {
                        old = "throw ptr",
                        newStr = "window.KSWASMSDK.KSUncaughtException(true)",
                    },
                };
                foreach (var rule in rules)
                {
                    text = Regex.Replace(text, rule.old, rule.newStr);
                }
            }

            if (text.Contains("UnityModule"))
            {
                text +=
                    ";if (typeof GameGlobal != \"undefined\") {GameGlobal.unityNamespace.UnityModule = UnityModule;}";
            }
            else if (text.Contains("unityFramework"))
            {
                text +=
                    ";if (typeof GameGlobal != \"undefined\") {GameGlobal.unityNamespace.UnityModule = unityFramework;}";
            }
            else if (text.Contains("tuanjieFramework"))
            {
                text +=
                    ";if (typeof GameGlobal != \"undefined\") {GameGlobal.unityNamespace.UnityModule = tuanjieFramework;}";
            }
            else if (UnityUtil.UseIL2CPP())
            {
                if (text.StartsWith("(") && text.EndsWith(")"))
                {
                    text = text.Substring(1, text.Length - 2);
                }

                text =
                    "if (typeof GameGlobal != \"undefined\") {GameGlobal.unityNamespace.UnityModule = "
                    + text
                    + ";}";
            }

            if (!Directory.Exists(Path.Combine(config.ProjectConf.DST, miniGameDir)))
            {
                Directory.CreateDirectory(Path.Combine(config.ProjectConf.DST, miniGameDir));
            }

            if (!Directory.Exists(Path.Combine(config.ProjectConf.DST, miniGameDir, frameworkDir)))
            {
                Directory.CreateDirectory(
                    Path.Combine(config.ProjectConf.DST, miniGameDir, frameworkDir)
                );
            }

            var header =
                "if (typeof window !== \"undefined\") {var OriginalAudioContext = window.AudioContext || window.webkitAudioContext;window.AudioContext = function() {if (this instanceof window.AudioContext) {return ks.createWebAudioContext();} else {return new OriginalAudioContext();}};}";

            if (config.CompileOptions.DevelopBuild)
            {
                header = header + RenderAnalysisRules.header;
                for (i = 0; i < RenderAnalysisRules.rules.Length; i++)
                {
                    var rule = RenderAnalysisRules.rules[i];
                    text = Regex.Replace(text, rule.old, rule.newStr);
                }
            }

            text = header + text;

            var targetPath = Path.Combine(config.ProjectConf.DST, miniGameDir, target);
            if (!UnityUtil.UseIL2CPP())
            {
                targetPath = Path.Combine(
                    config.ProjectConf.DST,
                    miniGameDir,
                    frameworkDir,
                    target
                );

                foreach (var rule in ReplaceRules.NativeRules)
                {
                    if (ShowMatchFailedWarning(text, rule.old, "native") == false)
                    {
                        text = Regex.Replace(text, rule.old, rule.newStr);
                    }
                }
            }

            text = Regex.Replace(text, "tj\\.", "ks.");

            File.WriteAllText(targetPath, text, new UTF8Encoding(false));

            UnityEngine.Debug.LogFormat("[Converter]  adapt framework done! ");
        }

        private static string GetWebGLDataPath()
        {
            if (KSExtEnvDef.GETDEF("UNITY_2020_1_OR_NEWER"))
            {
                return Path.Combine(config.ProjectConf.DST, webglDir, "Build", "webgl.data");
            }
            else
            {
                return Path.Combine(
                    config.ProjectConf.DST,
                    webglDir,
                    "Build",
                    "webgl.data.unityweb"
                );
            }
        }

        private static string[] GetWeixinMiniGameFilePath(string key)
        {
            var bootJson = Path.Combine(
                config.ProjectConf.DST,
                webglDir,
                "Code",
                "wwwroot",
                "_framework",
                "blazor.boot.json"
            );
            var boot = JsonMapper.ToObject(File.ReadAllText(bootJson, Encoding.UTF8));
            if (!boot.ContainsKey("environmentVariables"))
            {
                var jd = new JsonData();
                jd["INTERP_OPTS"] = "-jiterp";
                boot["environmentVariables"] = jd;
                JsonWriter writer = new JsonWriter();
                boot.ToJson(writer);
                File.WriteAllText(bootJson, writer.TextWriter.ToString());
                Debug.Log("Env INTERP_OPTS added to blazor.boot.json");
            }
            else if (!boot["environmentVariables"].ContainsKey("INTERP_OPTS"))
            {
                boot["environmentVariables"]["INTERP_OPTS"] = "-jiterp";
                JsonWriter writer = new JsonWriter();
                boot.ToJson(writer);
                File.WriteAllText(bootJson, writer.TextWriter.ToString());
                Debug.Log("Env INTERP_OPTS added to blazor.boot.json");
            }
            return boot["resources"]
                [key]
                .Keys.Select(file =>
                    Path.Combine(
                        config.ProjectConf.DST,
                        webglDir,
                        "Code",
                        "wwwroot",
                        "_framework",
                        file
                    )
                )
                .ToArray();
        }

        private static bool finishExport()
        {
            int code = GenerateBinFile();
            // bool succeed = true;

            if (code == 0)
            {
                if (
                    !convertDataPackage(false)
                    || !UnityUtil.RunBabel(Path.Combine(config.ProjectConf.DST, miniGameDir))
                )
                {
                    return false;
                }
                Debug.LogFormat("[Converter] All done!");
                LifeCycleEvent.Emit(LifeCycle.exportDone);
            }
            else
            {
                if (
                    !convertDataPackage(true)
                    || !UnityUtil.RunBabel(Path.Combine(config.ProjectConf.DST, miniGameDir))
                )
                {
                    return false;
                }
            }

            postProcessHandler?.Invoke();

            if (config.ProjectConf.assetLoadType == 0)
            {
                if (!UnityUtil.UploadInstantGameAssets(KSConvertCore.FirstBundlePath))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool convertDataPackage(bool brotliError)
        {
            var baseDataFilename = dataMd5 + ".webgl.data.unityweb.bin";
            var webglDirPath = Path.Combine(config.ProjectConf.DST, webglDir);
            var minigameDirPath = Path.Combine(config.ProjectConf.DST, miniGameDir);
            var minigameDataPath = Path.Combine(minigameDirPath, "data-package");

            var originDataFilename = baseDataFilename + ".txt";
            var originMinigameDataPath = Path.Combine(minigameDataPath, originDataFilename);
            var originTempDataPath = Path.Combine(webglDirPath, originDataFilename);

            var brDataFilename = baseDataFilename + ".br";
            var brMinigameDataPath = Path.Combine(minigameDataPath, brDataFilename);
            var tempDataBrPath = Path.Combine(webglDirPath, brDataFilename);

            var dataFilename = originDataFilename;
            var sourceDataPath = GetWebGLDataPath();
            var tempDataPath = originTempDataPath;
            var dataPackageBrotliRet = 0;
            if (brotliError)
            {
                if (config.ProjectConf.assetLoadType == 1)
                {
                    UnityEngine.Debug.LogWarning(
                        "brotli失败，无法检测文件大小，请上传资源文件到CDN"
                    );
                    config.ProjectConf.assetLoadType = 0;
                }
                Debug.LogError("Brotli压缩失败，请到转出目录手动压缩！");
                return false;
            }

            if (!!config.ProjectConf.compressDataPackage)
            {
                dataFilename = brDataFilename;
                tempDataPath = tempDataBrPath;
                UnityEngine.Debug.LogFormat("[Compressing] Starting to compress datapackage");
                dataPackageBrotliRet = Brotlib(dataFilename, sourceDataPath, tempDataPath);
                Debug.Log("[Compressing] compress ret = " + dataPackageBrotliRet);
                if (dataPackageBrotliRet != 0)
                {
                    config.ProjectConf.compressDataPackage = false;
                    dataFilename = originDataFilename;
                    tempDataPath = originTempDataPath;
                }
            }

            if (!config.ProjectConf.compressDataPackage || dataPackageBrotliRet != 0)
            {
                File.Copy(sourceDataPath, tempDataPath, true);
            }

            if (config.ProjectConf.assetLoadType == 1)
            {
                File.Copy(
                    tempDataPath,
                    config.ProjectConf.compressDataPackage
                        ? brMinigameDataPath
                        : originMinigameDataPath,
                    true
                );
            }

            checkNeedRmovePackageParallelPreload();
            // 设置InstantGame的首资源包路径，上传用
            FirstBundlePath = tempDataPath;
            var loadDataFromCdn = config.ProjectConf.assetLoadType == 0;
            Rule[] rules =
            {
                new Rule() { old = "$DEPLOY_URL", newStr = config.ProjectConf.CDN },
                new Rule()
                {
                    old = "$LOAD_DATA_FROM_SUBPACKAGE",
                    newStr = loadDataFromCdn ? "false" : "true",
                },
                new Rule()
                {
                    old = "$COMPRESS_DATA_PACKAGE",
                    newStr = config.ProjectConf.compressDataPackage ? "true" : "false",
                },
                new Rule()
                {
                    old = "$COMPRESS_WASM",
                    newStr = config.ProjectConf.compressWasm ? "true" : "false",
                },
                new Rule()
                {
                    old = "$DEMO_PACKAGE",
                    newStr = config.ProjectConf.demoPackage ? "true" : "false",
                },
            };
            string[] files = { "game.js", "game.json", "project.config.json", "check-version.js" };
            ReplaceFileContent(files, rules);
            return true;
        }

        private static void checkNeedRmovePackageParallelPreload()
        {
            if (config.ProjectConf.assetLoadType == 0)
            {
                var filePath = Path.Combine(config.ProjectConf.DST, miniGameDir, "game.json");

                string content = File.ReadAllText(filePath, Encoding.UTF8);
                JsonData gameJson = JsonMapper.ToObject(content);
                JsonWriter writer = new JsonWriter();
                writer.IndentValue = 2;
                writer.PrettyPrint = true;
                gameJson["parallelPreloadSubpackages"]
                    .Remove(gameJson["parallelPreloadSubpackages"][1]);

                gameJson.ToJson(writer);
                File.WriteAllText(filePath, writer.TextWriter.ToString());
            }
        }

        public static void ReplaceFileContent(string[] files, Rule[] replaceList)
        {
            if (files.Length != 0 && replaceList.Length != 0)
            {
                for (int i = 0; i < files.Length; i++)
                {
                    var filePath = Path.Combine(config.ProjectConf.DST, miniGameDir, files[i]);
                    string text = File.ReadAllText(filePath, Encoding.UTF8);
                    for (int j = 0; j < replaceList.Length; j++)
                    {
                        var rule = replaceList[j];
                        text = text.Replace(rule.old, rule.newStr);
                    }

                    File.WriteAllText(filePath, text, new UTF8Encoding(false));
                }
            }
        }

        private static string GetWebGLCodePath()
        {
            if (KSExtEnvDef.GETDEF("UNITY_2020_1_OR_NEWER"))
            {
                if (UnityUtil.UseIL2CPP())
                {
                    return Path.Combine(config.ProjectConf.DST, webglDir, "Build", "webgl.wasm");
                }
                else
                {
                    return GetWeixinMiniGameFilePath("wasmNative")[0];
                }
            }
            else
            {
                return Path.Combine(
                    config.ProjectConf.DST,
                    webglDir,
                    "Build",
                    "webgl.wasm.code.unityweb"
                );
            }
        }

        public static int GenerateBinFile(bool isFromConvert = false)
        {
            Debug.LogFormat("[Converter] Starting to genarate md5 and copy files");

            var codePath = GetWebGLCodePath();
            codeMd5 = UnityUtil.BuildFileMd5(codePath);
            var dataPath = GetWebGLDataPath();
            dataMd5 = UnityUtil.BuildFileMd5(dataPath);
            var symbolPath = GetWebGLSymbolPath();

            RemoveOldAssetPackage(Path.Combine(config.ProjectConf.DST, webglDir));
            RemoveOldAssetPackage(Path.Combine(config.ProjectConf.DST, webglDir + "-min"));
            var buildTemplate = new BuildTemplate(
                Path.Combine(UnityUtil.GetKsSDKRootPath(), "Runtime", "minigame-default"),
                Path.Combine(Application.dataPath, "KS-WASM-SDK-V2", "Editor", "template"),
                Path.Combine(config.ProjectConf.DST, miniGameDir)
            );
            buildTemplate.start();
            if (File.Exists(symbolPath))
            {
                File.Copy(
                    symbolPath,
                    Path.Combine(
                        config.ProjectConf.DST,
                        miniGameDir,
                        "webgl.wasm.symbols.unityweb"
                    ),
                    true
                );
            }

            var info = new FileInfo(dataPath);
            dataFileSize = info.Length.ToString();
            Debug.LogFormat("[Converter] that to genarate md5 and copy files ended");
            ModifyMiniGameConfigs(isFromConvert);
            ModifySDKFile();
            GameJsPlugins();
            if (
                !Directory.Exists(Path.Combine(config.ProjectConf.DST, webglDir, "StreamingAssets"))
            )
            {
                Directory.CreateDirectory(
                    Path.Combine(config.ProjectConf.DST, webglDir, "StreamingAssets")
                );
            }
            if (config.ProjectConf.compressWasm)
            {
                return Brotlib(
                    codeMd5 + ".webgl.wasm.code.unityweb.wasm.br",
                    codePath,
                    Path.Combine(
                        config.ProjectConf.DST,
                        miniGameDir,
                        "wasmcode",
                        codeMd5 + ".webgl.wasm.code.unityweb.wasm.br"
                    )
                );
            }
            else
            {
                File.Copy(
                    codePath,
                    Path.Combine(
                        config.ProjectConf.DST,
                        miniGameDir,
                        "wasmcode",
                        codeMd5 + ".webgl.wasm.code.unityweb.wasm"
                    ),
                    true
                );
                return 0;
            }
        }

        private static int Brotlib(string filename, string sourcePath, string targetPath)
        {
            Debug.LogFormat("[Converter] Starting to generate Brotlib file");
            var cachePath = Path.Combine(config.ProjectConf.DST, webglDir, filename);
            var shortFilename = filename.Substring(filename.IndexOf('.') + 1);

            if (File.Exists(cachePath) && lastBrotliType == config.CompileOptions.brotliMT)
            {
                File.Copy(cachePath, targetPath, true);
                return 0;
            }

            if (Directory.Exists(Path.Combine(config.ProjectConf.DST, webglDir)))
            {
                foreach (
                    string path in Directory.GetFiles(
                        Path.Combine(config.ProjectConf.DST, webglDir)
                    )
                )
                {
                    FileInfo fileInfo = new FileInfo(path);
                    if (fileInfo.Name.Contains(shortFilename))
                    {
                        File.Delete(fileInfo.FullName);
                    }
                }
            }

            if (config.CompileOptions.brotliMT)
            {
                MultiThreadBrotliCompress(sourcePath, targetPath);
            }
            else
            {
                UnityUtil.compressBrotli(sourcePath, targetPath);
            }

            if (targetPath != cachePath)
            {
                File.Copy(targetPath, cachePath, true);
            }
            return 0;
        }

        public static bool MultiThreadBrotliCompress(
            string sourcePath,
            string dstPath,
            int quality = 11,
            int window = 21,
            int maxCpuThreads = 0
        )
        {
            if (maxCpuThreads == 0)
                maxCpuThreads = Environment.ProcessorCount;
            var sourceBuffer = File.ReadAllBytes(sourcePath);
            byte[] outputBuffer = new byte[0];
            int ret = 0;
            if (sourceBuffer.Length > 50 * 1024 * 1024 && Path.GetExtension(sourcePath) == ".wasm") // 50MB以上的wasm压缩率低了可能导致小游戏包超过20MB，需提高压缩率
            {
                ret = BrotliEnc.CompressWasmMT(
                    sourceBuffer,
                    ref outputBuffer,
                    quality,
                    window,
                    maxCpuThreads
                );
            }
            else
            {
                ret = BrotliEnc.CompressBufferMT(
                    sourceBuffer,
                    ref outputBuffer,
                    quality,
                    window,
                    maxCpuThreads
                );
            }

            if (ret == 0)
            {
                using (
                    FileStream fileStream = new FileStream(
                        dstPath,
                        FileMode.Create,
                        FileAccess.Write
                    )
                )
                {
                    fileStream.Write(outputBuffer, 0, outputBuffer.Length);
                }
                return true;
            }
            else
            {
                Debug.LogError("CompressWasmMT failed");
                return false;
            }
        }

        private static void GameJsPlugins()
        {
            var filePath = Path.Combine(config.ProjectConf.DST, miniGameDir, "game.js");

            string content = File.ReadAllText(filePath, Encoding.UTF8);

            Regex regex = new Regex(@"^import .*;$", RegexOptions.Multiline);
            MatchCollection matches = regex.Matches(content);

            int lastIndex = 0;
            if (matches.Count > 0)
            {
                lastIndex = matches[matches.Count - 1].Index + matches[matches.Count - 1].Length;
            }

            bool changed = false;
            StringBuilder sb = new StringBuilder(content);
            if (config.ProjectConf.needCheckUpdate)
            {
                sb.Insert(lastIndex, Environment.NewLine + "import './plugins/check-update';");
                changed = true;
            }
            else
            {
                File.Delete(
                    Path.Combine(config.ProjectConf.DST, miniGameDir, "plugins", "check-update.js")
                );
            }
            if (config.CompileOptions.autoAdaptScreen)
            {
                sb.Insert(lastIndex, Environment.NewLine + "import './plugins/screen-adapter';");
                changed = true;
            }
            else
            {
                File.Delete(
                    Path.Combine(
                        config.ProjectConf.DST,
                        miniGameDir,
                        "plugins",
                        "screen-adapter.js"
                    )
                );
            }

            if (changed)
            {
                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            }
            else
            {
                Directory.Delete(
                    Path.Combine(config.ProjectConf.DST, miniGameDir, "plugins"),
                    true
                );
            }
        }

        private static void ModifySDKFile()
        {
            string content = File.ReadAllText(SDKFilePath, Encoding.UTF8);
            content = content.Replace("$unityVersion$", Application.unityVersion);
            File.WriteAllText(
                Path.Combine(config.ProjectConf.DST, miniGameDir, "unity-sdk", "index.js"),
                content,
                Encoding.UTF8
            );
            content = File.ReadAllText(
                Path.Combine(
                    UnityUtil.GetKsSDKRootPath(),
                    "Runtime",
                    "minigame-default",
                    "unity-sdk",
                    "storage.js"
                ),
                Encoding.UTF8
            );
            var PreLoadKeys =
                config.PlayerPrefsKeys.Count > 0 ? JsonMapper.ToJson(config.PlayerPrefsKeys) : "[]";
            content = content.Replace("'$PreLoadKeys'", PreLoadKeys);
            File.WriteAllText(
                Path.Combine(config.ProjectConf.DST, miniGameDir, "unity-sdk", "storage.js"),
                content,
                Encoding.UTF8
            );
            content = File.ReadAllText(
                Path.Combine(
                    UnityUtil.GetKsSDKRootPath(),
                    "Runtime",
                    "minigame-default",
                    "unity-sdk",
                    "texture.js"
                ),
                Encoding.UTF8
            );
            File.WriteAllText(
                Path.Combine(config.ProjectConf.DST, miniGameDir, "unity-sdk", "texture.js"),
                content,
                Encoding.UTF8
            );
        }

        public static string HandleLoadingImage()
        {
            var info = AssetDatabase.LoadAssetAtPath<Texture>(config.ProjectConf.bgImageSrc);
            var oldFilename = Path.GetFileName(defaultImgSrc);
            var newFilename = Path.GetFileName(config.ProjectConf.bgImageSrc);
            if (config.ProjectConf.bgImageSrc != defaultImgSrc)
            {
                if (info.width > 2048 || info.height > 2048)
                {
                    throw new Exception("封面图宽高不可超过2048");
                }

                File.Delete(
                    Path.Combine(config.ProjectConf.DST, miniGameDir, "images", oldFilename)
                );
                File.Copy(
                    config.ProjectConf.bgImageSrc,
                    Path.Combine(config.ProjectConf.DST, miniGameDir, "images", newFilename),
                    true
                );
                return "images/" + Path.GetFileName(config.ProjectConf.bgImageSrc);
            }
            else
            {
                return "images/" + Path.GetFileName(defaultImgSrc);
            }
        }

        public static string GetArrayString(string inp)
        {
            var result = string.Empty;
            var iterms = new List<string>(inp.Split(new char[] { ';' }));
            iterms.ForEach(
                (iterm) =>
                {
                    if (!string.IsNullOrEmpty(iterm.Trim()))
                    {
                        result += "\"" + iterm.Trim() + "\", ";
                    }
                }
            );
            if (!string.IsNullOrEmpty(result))
            {
                result = result.Substring(0, result.Length - 2);
            }

            return result;
        }

        private class PreloadFile
        {
            public PreloadFile(string fn, string rp)
            {
                fileName = fn;
                relativePath = rp;
            }

            public string fileName;
            public string relativePath;
        }

        private static string GetPreloadList(string strPreloadfiles)
        {
            if (strPreloadfiles == string.Empty)
            {
                return string.Empty;
            }

            string preloadList = string.Empty;
            var streamingAssetsPath = Path.Combine(
                config.ProjectConf.DST,
                webglDir + "/StreamingAssets"
            );
            var fileNames = strPreloadfiles.Split(new char[] { ';' });
            List<PreloadFile> preloadFiles = new List<PreloadFile>();
            foreach (var fileName in fileNames)
            {
                if (fileName.Trim() == string.Empty)
                {
                    continue;
                }

                preloadFiles.Add(new PreloadFile(fileName, string.Empty));
            }

            if (Directory.Exists(streamingAssetsPath))
            {
                foreach (
                    string path in Directory.GetFiles(
                        streamingAssetsPath,
                        "*",
                        SearchOption.AllDirectories
                    )
                )
                {
                    FileInfo fileInfo = new FileInfo(path);
                    foreach (var preloadFile in preloadFiles)
                    {
                        if (fileInfo.Name.Contains(preloadFile.fileName))
                        {
                            var relativePath = path.Substring(streamingAssetsPath.Length + 1)
                                .Replace('\\', '/');
                            preloadFile.relativePath = relativePath;
                            break;
                        }
                    }
                }
            }
            else
            {
                Debug.LogError("没有找到StreamingAssets目录， 无法生成预下载列表");
            }

            foreach (var preloadFile in preloadFiles)
            {
                if (preloadFile.relativePath == string.Empty)
                {
                    Debug.LogError($"并非所有预下载的文件都被找到，剩余：{preloadFile.fileName}");
                    continue;
                }

                preloadList += "\"" + preloadFile.relativePath + "\", \r";
            }

            return preloadList;
        }

        private static string GetCustomUnicodeRange(string customUnicode)
        {
            if (customUnicode == string.Empty)
            {
                return "[]";
            }

            List<int> unicodeCodes = new List<int>();
            foreach (char c in customUnicode)
            {
                unicodeCodes.Add(char.ConvertToUtf32(c.ToString(), 0));
            }

            unicodeCodes.Sort();

            List<Tuple<int, int>> ranges = new List<Tuple<int, int>>();
            int startRange = unicodeCodes[0];
            int endRange = unicodeCodes[0];

            for (int i = 1; i < unicodeCodes.Count; i++)
            {
                if (unicodeCodes[i] == endRange)
                {
                    continue;
                }
                else if (unicodeCodes[i] == endRange + 1)
                {
                    endRange = unicodeCodes[i];
                }
                else
                {
                    ranges.Add(Tuple.Create(startRange, endRange));
                    startRange = endRange = unicodeCodes[i];
                }
            }
            ranges.Add(Tuple.Create(startRange, endRange));

            StringBuilder ret = new StringBuilder();
            foreach (var range in ranges)
            {
                ret.AppendFormat("[0x{0:X}, 0x{1:X}], ", range.Item1, range.Item2);
            }
            ret.Length -= 2;
            ret.Insert(0, "[");
            ret.Append("]");

            return ret.ToString();
        }

        private static string GenerateBootInfo()
        {
            StringBuilder sb = new StringBuilder();
            var host = Dns.GetHostEntry("");
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    sb.Append($"player-connection-ip={ip.ToString()}");
                    break;
                }
            }

            return sb.ToString();
        }

        public static string VersionDateFormatter(string version)
        {
            string year = version.Substring(0, 4);
            string month = version.Substring(4, 2);
            string day = version.Substring(6, 2);

            return $"{year}.{int.Parse(month)}.{int.Parse(day)}";
        }

        public static void ModifyMiniGameConfigs(bool isFromConvert = false)
        {
            Debug.LogFormat("[Converter] Starting to modify configs");

            var PRELOAD_LIST = GetPreloadList(config.ProjectConf.preloadFiles);
            var imgSrc = HandleLoadingImage();

            var bundlePathIdentifierStr = GetArrayString(config.ProjectConf.bundlePathIdentifier);
            var excludeFileExtensionsStr = GetArrayString(
                config.ProjectConf.bundleExcludeExtensions
            );

            var screenOrientation = new List<string>()
            {
                "portrait",
                "landscape",
                "landscapeLeft",
                "landscapeRight",
            }[(int)config.ProjectConf.Orientation];

            var customUnicodeRange = GetCustomUnicodeRange(config.FontOptions.CustomUnicode);
            Debug.Log("customUnicodeRange: " + customUnicodeRange);

            var boolConfigInfo = GenerateBootInfo();

            var companyName = Application.companyName;
            var productName = Application.productName;
            var productVersion = Application.version;

            Rule[] replaceArrayList = ReplaceRules.GenRules(
                new string[]
                {
                    productName == string.Empty ? "webgl" : productName,
                    config.ProjectConf.Appid,
                    screenOrientation,
                    config.CompileOptions.enableIOSPerformancePlus ? "true" : "false",
                    config.ProjectConf.VideoUrl,
                    codeMd5,
                    dataMd5,
                    config.ProjectConf.StreamCDN,
                    config.ProjectConf.CDN + "/Assets",
                    PRELOAD_LIST,
                    imgSrc,
                    config.ProjectConf.HideAfterCallMain ? "true" : "false",
                    config.ProjectConf.bundleHashLength.ToString(),
                    bundlePathIdentifierStr,
                    excludeFileExtensionsStr,
                    config.CompileOptions.Webgl2 ? "2" : "1",
                    Application.unityVersion,
                    KSExtEnvDef.pluginVersion,
                    config.ProjectConf.dataFileSubPrefix,
                    config.ProjectConf.maxStorage.ToString(),
                    config.ProjectConf.defaultReleaseSize.ToString(),
                    config.ProjectConf.texturesHashLength.ToString(),
                    config.ProjectConf.texturesPath,
                    config.ProjectConf.needCacheTextures ? "true" : "false",
                    config.ProjectConf.loadingBarWidth.ToString(),
                    GetColorSpace(),
                    config.ProjectConf.disableHighPerformanceFallback ? "true" : "false",
                    config.SDKOptions.PreloadKSFont ? "true" : "false",
                    config.CompileOptions.showMonitorSuggestModal ? "true" : "false",
                    config.CompileOptions.enableProfileStats ? "true" : "false",
                    config.CompileOptions.iOSAutoGCInterval.ToString(),
                    dataFileSize,
                    IsInstantGameAutoStreaming() ? "true" : "false",
                    (
                        config.CompileOptions.DevelopBuild
                        && config.CompileOptions.enableRenderAnalysis
                    )
                        ? "true"
                        : "false",
                    config.ProjectConf.IOSDevicePixelRatio.ToString(),
                    UnityUtil.UseIL2CPP() ? "" : "/framework",
                    UnityUtil.UseIL2CPP() ? "false" : "true",
                    config.CompileOptions.brotliMT ? "true" : "false",
                    // FontOptions
                    config.FontOptions.CJK_Unified_Ideographs
                        ? "true"
                        : "false",
                    config.FontOptions.C0_Controls_and_Basic_Latin ? "true" : "false",
                    config.FontOptions.CJK_Symbols_and_Punctuation ? "true" : "false",
                    config.FontOptions.General_Punctuation ? "true" : "false",
                    config.FontOptions.Enclosed_CJK_Letters_and_Months ? "true" : "false",
                    config.FontOptions.Vertical_Forms ? "true" : "false",
                    config.FontOptions.CJK_Compatibility_Forms ? "true" : "false",
                    config.FontOptions.Miscellaneous_Symbols ? "true" : "false",
                    config.FontOptions.CJK_Compatibility ? "true" : "false",
                    config.FontOptions.Halfwidth_and_Fullwidth_Forms ? "true" : "false",
                    config.FontOptions.Dingbats ? "true" : "false",
                    config.FontOptions.Letterlike_Symbols ? "true" : "false",
                    config.FontOptions.Enclosed_Alphanumerics ? "true" : "false",
                    config.FontOptions.Number_Forms ? "true" : "false",
                    config.FontOptions.Currency_Symbols ? "true" : "false",
                    config.FontOptions.Arrows ? "true" : "false",
                    config.FontOptions.Geometric_Shapes ? "true" : "false",
                    config.FontOptions.Mathematical_Operators ? "true" : "false",
                    customUnicodeRange,
                    boolConfigInfo,
                    companyName,
                    productName,
                    string.IsNullOrEmpty(config.ProjectConf.buildVersion)
                        ? productVersion
                        : config.ProjectConf.buildVersion,
                    isSupportWasmSplit ? "true" : "false",
                    KSExtEnvDef.scriptVersion,
                    KSExtEnvDef.converterVersion,
                    config.ProjectConf.buildDescription,
                    config.ProjectConf.isCoverviewCustomized ? "true" : "false",
                    $"{VersionDateFormatter(KSExtEnvDef.pluginVersion)}{KSExtEnvDef.packageVersion.Replace(".", "")}",
                }
            );

            List<Rule> replaceList = new List<Rule>(replaceArrayList);
            List<string> files = new List<string>
            {
                "game.js",
                "game.json",
                "project.config.json",
                "unity-namespace.js",
                "check-version.js",
                "unity-sdk/font/index.js",
            };

            ReplaceFileContent(files.ToArray(), replaceList.ToArray());
            BuildTemplate.MergeConfigurationFiles(
                Path.Combine(
                    Application.dataPath,
                    "KS-WASM-SDK-V2",
                    "Editor",
                    "template",
                    "minigame"
                ),
                Path.Combine(config.ProjectConf.DST, miniGameDir)
            );
            LifeCycleEvent.Emit(LifeCycle.afterBuildTemplate);
            UnityEngine.Debug.LogFormat("[Converter] that to modify configs ended");
        }

        private static string GetColorSpace()
        {
            switch (PlayerSettings.colorSpace)
            {
                case ColorSpace.Gamma:
                    return "Gamma";
                case ColorSpace.Linear:
                    return "Linear";
                case ColorSpace.Uninitialized:
                    return "Uninitialized";
                default:
                    return "Unknow";
            }
        }

        private static void RemoveOldAssetPackage(string dstDir)
        {
            try
            {
                if (Directory.Exists(dstDir))
                {
                    foreach (string path in Directory.GetFiles(dstDir))
                    {
                        FileInfo fileInfo = new FileInfo(path);
                        if (
                            fileInfo.Name.Contains("webgl.data.unityweb.bin.txt")
                            || fileInfo.Name.Contains("webgl.data.unityweb.bin.br")
                        )
                        {
                            File.Delete(fileInfo.FullName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError(ex);
            }
        }

        private static string GetWebGLSymbolPath()
        {
            if (KSExtEnvDef.GETDEF("UNITY_2020_1_OR_NEWER"))
            {
                return Path.Combine(
                    config.ProjectConf.DST,
                    webglDir,
                    "Build",
                    "webgl.symbols.json"
                );
            }
            else
            {
                return Path.Combine(
                    config.ProjectConf.DST,
                    webglDir,
                    "Build",
                    "webgl.wasm.symbols.unityweb"
                );
            }
        }

        public static void UpdateGraphicAPI()
        {
            GraphicsDeviceType[] targets = new GraphicsDeviceType[] { };
#if PLATFORM_WEIXINMINIGAME
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WeixinMiniGame, false);
            if (config.CompileOptions.Webgl2)
            {
                PlayerSettings.SetGraphicsAPIs(
                    BuildTarget.WeixinMiniGame,
                    new GraphicsDeviceType[] { GraphicsDeviceType.OpenGLES3 }
                );
            }
            else
            {
                PlayerSettings.SetGraphicsAPIs(
                    BuildTarget.WeixinMiniGame,
                    new GraphicsDeviceType[] { GraphicsDeviceType.OpenGLES2 }
                );
            }
#else
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
            if (config.CompileOptions.Webgl2)
            {
                PlayerSettings.SetGraphicsAPIs(
                    BuildTarget.WebGL,
                    new GraphicsDeviceType[] { GraphicsDeviceType.OpenGLES3 }
                );
            }
            else
            {
                PlayerSettings.SetGraphicsAPIs(
                    BuildTarget.WebGL,
                    new GraphicsDeviceType[] { GraphicsDeviceType.OpenGLES2 }
                );
            }
#endif
        }

        public static bool IsInstantGameAutoStreaming()
        {
            if (string.IsNullOrEmpty(UnityUtil.GetInstantGameAutoStreamingCDN()))
            {
                return false;
            }
            return true;
        }

        public static bool CheckSDK()
        {
            string dir = Path.Combine(Application.dataPath, "KS-WASM-SDK");
            if (Directory.Exists(dir))
            {
                return false;
            }
            return true;
        }

        public static bool ShowMatchFailedWarning(string text, string rule, string file)
        {
            if (Regex.IsMatch(text, rule) == false)
            {
                Debug.Log($"UnMatched {file} rule: {rule}");
                return true;
            }
            return false;
        }
    }
}
