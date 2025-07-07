// using UnityEditor;
// using UnityEditor.PackageManager;
// using UnityEditor.PackageManager.Requests;
// using UnityEngine;
// using System;

// namespace KSWASM.editor
// {
// 	public class PackageUpdateManager
// 	{
// 		private const string packageName = "com.kuaishou.minigame";

// 		private static ListRequest listRequest;
// 		private static SearchRequest searchRequest;
// 		private static AddRequest addRequest;

// 		private static bool isChecked = false;

// 		private static string currentVersion = null;
// 		private static string latestVersion = null;

// 		public static int CompareVersions(string v1, string v2)
// 		{
// 			if (v1 == v2)
// 				return 0;

// 			string[] parts1 = v1.Split('.');
// 			string[] parts2 = v2.Split('.');

// 			int length = Mathf.Max(parts1.Length, parts2.Length);

// 			for (int i = 0; i < length; i++)
// 			{
// 				int num1 = i < parts1.Length ? int.Parse(parts1[i]) : 0;
// 				int num2 = i < parts2.Length ? int.Parse(parts2[i]) : 0;

// 				if (num1 < num2)
// 					return -1;
// 				if (num1 > num2)
// 					return 1;
// 			}

// 			return 0;
// 		}

// 		public static bool CheckUpdateOnce()
// 		{
// 			if (isChecked)
// 			{
// 				return false;
// 			}

// 			CheckUpdate();
// 			return true;
// 		}

// 		[MenuItem("快手小游戏/检查更新", false, 1000)]
// 		public static void ShowWindow()
// 		{
// 			CheckUpdate(true);
// 			isWindow = true;
// 		}
		
// 		private static bool isWindow = false;
		
// 		public static void CheckUpdate(bool _isWindow=false)
// 		{
// 			isWindow = _isWindow;
// 			isChecked = true;
// 			listRequest = Client.List();
// 			EditorApplication.update += ListPackages;
// 		}

// 		private static void ListPackages()
// 		{
// 			if (!listRequest.IsCompleted)
// 				return;

// 			EditorApplication.update -= ListPackages;

// 			if (listRequest.Status == StatusCode.Success)
// 			{
// 				foreach (var package in listRequest.Result)
// 				{
// 					if (package.name == packageName)
// 					{
// 						currentVersion = package.version;
// 						break;
// 					}
// 				}

// 				if (currentVersion != null)
// 				{
// 					searchRequest = Client.Search(packageName);
// 					EditorApplication.update += SearchProgress;
// 				}
// 			}
// 			else
// 			{
// 				Debug.LogError("获取已安装包列表失败: " + listRequest.Error.message);
// 			}
// 		}

// 		private static void SearchProgress()
// 		{
// 			if (!searchRequest.IsCompleted)
// 				return;

// 			EditorApplication.update -= SearchProgress;

// 			if (searchRequest.Status == StatusCode.Success)
// 			{
// 				foreach (var package in searchRequest.Result)
// 				{
// 					if (package.name == packageName)
// 					{
// 						latestVersion = package.versions.latestCompatible;
// 						break;
// 					}
// 				}

// 				int result = CompareVersions(latestVersion, currentVersion);

// 				if (result > 0)
// 				{
// 					if (EditorUtility.DisplayDialog("快手小游戏插件更新提示", $@"有新版本可用：{latestVersion} （当前版本：{currentVersion}），是否更新插件？", "是[推荐]", "否"))
// 					{
// 						addRequest = Client.Add($"{packageName}@{latestVersion}");
// 						EditorApplication.update += UpdateProgress;
// 					}
// 				}
// 				else if(isWindow)
// 				{
// 					EditorUtility.DisplayDialog("快手小游戏插件更新提示", $"版本一致，无需更新。", "好的");
// 				}
// 			}
// 			else
// 			{
// 				Debug.LogError("搜索远程包失败: " + searchRequest.Error.message);
// 			}
// 		}

// 		private static void UpdateProgress()
// 		{
// 			EditorUtility.DisplayProgressBar("Hold on", $"请稍候，更新 {packageName}...", 0.5f);
// 			if (!addRequest.IsCompleted) return;

// 			EditorApplication.update -= UpdateProgress;
// 			EditorUtility.ClearProgressBar();
// 		}
// 	}
// }