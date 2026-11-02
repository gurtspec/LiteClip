// LiteClip - translations.
// A language is a plain text file in the "lang" folder next to LiteClip.exe, one line per text:
//     English text => translated text
// Anything missing stays in English, so a half-finished translation still works.
// lang\template.txt (made by build.ps1) lists every text in the app.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace LiteClip
{
    public static class L
    {
        static Dictionary<string, string> map = new Dictionary<string, string>();

        // Translated text (or the English text if there's no translation).
        public static string T(string english)
        {
            string t;
            return english != null && map.TryGetValue(english, out t) ? t : english;
        }

        // Translated text with {0}, {1}... filled in.
        public static string T(string english, params object[] values)
        {
            try { return string.Format(T(english), values); }
            catch (FormatException) { return string.Format(english, values); }   // broken translation: fall back to English
        }

        // Languages you can pick: English plus every .txt in the lang folder (except the template).
        public static List<string> Available()
        {
            var list = new List<string> { "English" };
            try
            {
                if (Directory.Exists(Paths.Languages))
                    list.AddRange(Directory.GetFiles(Paths.Languages, "*.txt")
                        .Select(f => Path.GetFileNameWithoutExtension(f))
                        .Where(n => !n.Equals("template", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(n => n));
            }
            catch { }
            return list;
        }

        // Name shown for a language file: its "language = ..." line, or the file name.
        public static string DisplayName(string language)
        {
            if (language == "English") return "English";
            foreach (var pair in Read(language)) if (pair.Key == "language") return pair.Value;
            return language;
        }

        public static void Load(string language)
        {
            var m = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(language) && language != "English")
                foreach (var pair in Read(language)) m[pair.Key] = pair.Value;
            map = m;
        }

        static IEnumerable<KeyValuePair<string, string>> Read(string language)
        {
            var result = new List<KeyValuePair<string, string>>();
            try
            {
                // only plain file names, never paths
                if (language.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return result;
                string file = Path.Combine(Paths.Languages, language + ".txt");
                if (!File.Exists(file)) return result;
                foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
                {
                    if (raw.TrimStart().StartsWith("#")) continue;
                    int i = raw.IndexOf("=>", StringComparison.Ordinal);
                    if (i <= 0) continue;
                    string key = Unescape(raw.Substring(0, i).Trim()), value = Unescape(raw.Substring(i + 2).Trim());
                    if (key.Length > 0 && value.Length > 0) result.Add(new KeyValuePair<string, string>(key, value));
                }
            }
            catch { }
            return result;
        }

        static string Unescape(string s) { return s.Replace("\\n", "\n"); }

        // Translates the texts of a window section loaded from a .xaml file.
        public static void Apply(DependencyObject root)
        {
            if (map.Count == 0 || root == null) return;
            var text = root as TextBlock;
            if (text != null && text.Inlines.Count <= 1) text.Text = T(text.Text);
            var content = root as ContentControl;
            if (content != null && content.Content is string) content.Content = T((string)content.Content);
            var element = root as FrameworkElement;
            if (element != null && element.ToolTip is string) element.ToolTip = T((string)element.ToolTip);
            foreach (var child in LogicalTreeHelper.GetChildren(root))
            {
                var d = child as DependencyObject;
                if (d != null) Apply(d);
            }
        }
    }
}
