using System.IO;
using UnityEditor;
using UnityEngine;

namespace KSWASM.editor
{
    public class MiniHostBuildWindow : EditorWindow
    {
        [MenuItem("快手小游戏/构建", false, 10)]
        static void ShowWindow()
        {
            // PackageUpdateManager.CheckUpdate();
            var win = GetWindow(typeof(MiniHostBuildWindow), false, "构建快手小游戏");
            win.minSize = new Vector2(750, 400);
            win.position = new Rect(100, 100, 750, 400);
            win.Show();
        }

        private void OnGUI()
        {
            MiniHostBuildWindowHelper.OnSettingsGUI(true);
            OnBuildButtonGUI();
        }
        
        public void OnDisable()
        {
            MiniHostBuildWindowHelper.OnDisable();
        }

        private void OnBuildButtonGUI()
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var toBuild = GUILayout.Button(new GUIContent("生成并转换"), GUILayout.Width(100), GUILayout.Height(25));
            GUILayout.Space(20);
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
            if (toBuild)
            {
                var selection = Selection.objects;                
                KSEditorScriptObject config = UnityUtil.GetEditorConf("kuaishou", "Assets/KS-WASM-SDK-V2/Editor/MiniGameConfig.asset");
                
                // if (string.IsNullOrEmpty(config.ProjectConf.buildVersion) && string.IsNullOrEmpty(Application.version))
                // {
                //     Debug.LogError("版本不能为空。请填写版本，或者在 Player Settings 中填写 Version.");
                //     return;
                // }

                config.buildOptions = UnityUtil.GetBuildOptions(config);
                MiniHostBuildWindowHelper.UpdateWebGL2();
                if (KSConvertCore.DoExport(config) == KSConvertCore.KSExportError.SUCCEED)
                {
                    var zipped = Path.Combine(config.ProjectConf.DST, "game.zip");
                    UnityUtil.ZipGame(zipped, Path.Combine(config.ProjectConf.DST, "minigame"));
                }
                Selection.objects = selection;
                GUIUtility.ExitGUI();
            }
        }
    }
}