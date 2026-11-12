using System;
using UnityEditor;
using UnityEngine;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;

namespace VrcGitSync.VRChat
{
    /// <summary>
    /// VRChat SDK のアップロードに連動する。
    /// アップロードが成功したら、その時点の状態を記録して upload/アバター名/日時 のタグを付ける。
    /// ビルド・アップロード中は「フォルダの外に保存」の警告を止める。
    /// </summary>
    [InitializeOnLoad]
    static class UploadHook
    {
        static IVRCSdkAvatarBuilderApi _builder;
        static string _lastAvatarName;

        static UploadHook()
        {
            VRCSdkControlPanel.OnSdkPanelEnable -= OnPanelEnable;
            VRCSdkControlPanel.OnSdkPanelEnable += OnPanelEnable;
            SyncHooks.IsExternalBuildRunning = IsBuilding;

            // スクリプトの再読み込み時に SDK パネルがすでに開いている場合
            EditorApplication.delayCall += () =>
            {
                if (EditorWindow.HasOpenInstances<VRCSdkControlPanel>()) Attach();
            };
        }

        static void OnPanelEnable(object sender, EventArgs e)
        {
            EditorApplication.delayCall -= Attach;
            EditorApplication.delayCall += Attach;
        }

        static void Attach()
        {
            try
            {
                if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder) || builder == null) return;
                if (ReferenceEquals(builder, _builder)) return;

                if (_builder != null)
                {
                    _builder.OnSdkBuildStart -= OnBuildStart;
                    _builder.OnSdkUploadSuccess -= OnUploadSuccess;
                }
                _builder = builder;
                _builder.OnSdkBuildStart += OnBuildStart;
                _builder.OnSdkUploadSuccess += OnUploadSuccess;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VRC Git Sync] VRChat SDK との連携を設定できませんでした: " + e.Message);
            }
        }

        static bool IsBuilding()
        {
            var b = _builder;
            if (b == null) return false;
            return b.BuildState == SdkBuildState.Building || b.UploadState == SdkUploadState.Uploading;
        }

        static void OnBuildStart(object sender, object target)
        {
            if (target is GameObject go) _lastAvatarName = go.name;
            else if (target is Component c) _lastAvatarName = c.gameObject.name;
        }

        static void OnUploadSuccess(object sender, string avatarId)
        {
            string name = _lastAvatarName;
            if (string.IsNullOrEmpty(name))
            {
                try
                {
                    var selected = _builder != null ? _builder.SelectedAvatar : null;
                    if (selected != null) name = selected.name;
                }
                catch
                {
                    // 取れなくても記録はする
                }
            }
            if (string.IsNullOrEmpty(name)) name = "avatar";

            // SDK の処理が終わってから記録する
            EditorApplication.delayCall += () => _ = SyncOperations.CommitUpload(name, avatarId ?? "");
        }
    }
}
