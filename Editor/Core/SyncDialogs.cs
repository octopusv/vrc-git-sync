using System;
using UnityEditor;

namespace VrcGitSync
{
    /// <summary>
    /// 同期処理の途中で出す確認ダイアログ。
    /// 自動テストなどでダイアログを出せないときは、Override を設定して答えを返す。
    /// </summary>
    public static class SyncDialogs
    {
        /// <summary>(メッセージ, OKボタン, キャンセルボタン) → OK なら true。</summary>
        public static Func<string, string, string, bool> ConfirmOverride;

        /// <summary>(メッセージ, OKボタン, キャンセルボタン, 3つ目のボタン) → 0: OK、1: キャンセル、2: 3つ目。</summary>
        public static Func<string, string, string, string, int> ChoiceOverride;

        public static bool Confirm(string message, string ok, string cancel)
        {
            if (ConfirmOverride != null) return ConfirmOverride(message, ok, cancel);
            return EditorUtility.DisplayDialog(SyncOperations.Title, message, ok, cancel);
        }

        public static int Choice(string message, string ok, string cancel, string alt)
        {
            if (ChoiceOverride != null) return ChoiceOverride(message, ok, cancel, alt);
            return EditorUtility.DisplayDialogComplex(SyncOperations.Title, message, ok, cancel, alt);
        }

        public static void Notice(string message)
        {
            if (ConfirmOverride != null)
            {
                ConfirmOverride(message, "OK", null);
                return;
            }
            EditorUtility.DisplayDialog(SyncOperations.Title, message, "OK");
        }
    }
}
