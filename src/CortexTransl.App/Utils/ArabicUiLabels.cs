using System.Text;
using System.Text.RegularExpressions;

namespace CortexTransl.App.Utils;

// Short UI labels need UI terminology, not the sentence meanings of words
// such as Play, Save, Steam, or Brave. These rules apply only to English menus.
internal static class ArabicUiLabels
{
    private static readonly Dictionary<string, string> Terms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["New Game"] = "لعبة جديدة",
        ["Start Game"] = "بدء اللعبة",
        ["Continue"] = "متابعة",
        ["Continue Game"] = "متابعة اللعبة",
        ["Resume"] = "استئناف",
        ["Resume Game"] = "استئناف اللعبة",
        ["Play"] = "العب",
        ["Play Game"] = "بدء اللعب",
        ["Start"] = "بدء",
        ["Pause"] = "إيقاف مؤقت",
        ["Restart"] = "إعادة البدء",
        ["Retry"] = "إعادة المحاولة",
        ["Load"] = "تحميل",
        ["Load Game"] = "تحميل لعبة محفوظة",
        ["Save"] = "حفظ",
        ["Save Game"] = "حفظ اللعبة",
        ["Save and Exit"] = "حفظ وخروج",
        ["Save & Exit"] = "حفظ وخروج",
        ["Quick Save"] = "حفظ سريع",
        ["Quick Load"] = "تحميل سريع",
        ["Autosave"] = "حفظ تلقائي",
        ["Main Menu"] = "القائمة الرئيسية",
        ["Menu"] = "القائمة",
        ["Options"] = "الخيارات",
        ["Settings"] = "الإعدادات",
        ["Preferences"] = "التفضيلات",
        ["Game Settings"] = "إعدادات اللعبة",
        ["Gameplay"] = "أسلوب اللعب",
        ["Difficulty"] = "مستوى الصعوبة",
        ["Easy"] = "سهل",
        ["Normal"] = "عادي",
        ["Hard"] = "صعب",
        ["Very Hard"] = "صعب جدًا",
        ["Single Player"] = "لاعب واحد",
        ["Singleplayer"] = "لاعب واحد",
        ["Multiplayer"] = "اللعب الجماعي",
        ["Co-op"] = "اللعب التعاوني",
        ["Campaign"] = "طور القصة",
        ["Tutorial"] = "التدريب",
        ["Back"] = "رجوع",
        ["Return"] = "عودة",
        ["Return to Game"] = "العودة إلى اللعبة",
        ["Return to Main Menu"] = "العودة إلى القائمة الرئيسية",
        ["Quit"] = "خروج",
        ["Quit Game"] = "الخروج من اللعبة",
        ["Exit"] = "خروج",
        ["Exit Game"] = "الخروج من اللعبة",
        ["Quit to Desktop"] = "الخروج إلى سطح المكتب",
        ["Exit to Desktop"] = "الخروج إلى سطح المكتب",
        ["Apply"] = "تطبيق",
        ["Apply Changes"] = "تطبيق التغييرات",
        ["Cancel"] = "إلغاء",
        ["Confirm"] = "تأكيد",
        ["OK"] = "موافق",
        ["Yes"] = "نعم",
        ["No"] = "لا",
        ["Accept"] = "قبول",
        ["Decline"] = "رفض",
        ["Next"] = "التالي",
        ["Previous"] = "السابق",
        ["Close"] = "إغلاق",
        ["Open"] = "فتح",
        ["Delete"] = "حذف",
        ["Reset"] = "إعادة ضبط",
        ["Restore Defaults"] = "استعادة الإعدادات الافتراضية",
        ["Default"] = "افتراضي",
        ["On"] = "تشغيل",
        ["Off"] = "إيقاف",
        ["Enabled"] = "مفعّل",
        ["Disabled"] = "معطّل",
        ["Enable"] = "تفعيل",
        ["Disable"] = "تعطيل",
        ["Graphics"] = "الرسومات",
        ["Graphics Settings"] = "إعدادات الرسومات",
        ["Video"] = "الفيديو",
        ["Display"] = "العرض",
        ["Display Settings"] = "إعدادات العرض",
        ["Resolution"] = "دقة العرض",
        ["Screen Resolution"] = "دقة الشاشة",
        ["Display Mode"] = "وضع العرض",
        ["Window Mode"] = "وضع النافذة",
        ["Fullscreen"] = "ملء الشاشة",
        ["Full Screen"] = "ملء الشاشة",
        ["Windowed"] = "نافذة",
        ["Borderless"] = "بلا حدود",
        ["Borderless Windowed"] = "نافذة بلا حدود",
        ["VSync"] = "المزامنة الرأسية",
        ["V-Sync"] = "المزامنة الرأسية",
        ["Vertical Sync"] = "المزامنة الرأسية",
        ["Frame Rate"] = "معدل الإطارات",
        ["Frame Rate Limit"] = "الحد الأقصى لمعدل الإطارات",
        ["Refresh Rate"] = "معدل التحديث",
        ["Brightness"] = "السطوع",
        ["Contrast"] = "التباين",
        ["Field of View"] = "مجال الرؤية",
        ["Motion Blur"] = "ضبابية الحركة",
        ["Anti-Aliasing"] = "تنعيم الحواف",
        ["Texture Quality"] = "جودة الخامات",
        ["Shadow Quality"] = "جودة الظلال",
        ["Low"] = "منخفض",
        ["Medium"] = "متوسط",
        ["High"] = "مرتفع",
        ["Ultra"] = "فائق",
        ["Audio"] = "الصوت",
        ["Audio Settings"] = "إعدادات الصوت",
        ["Sound"] = "الصوت",
        ["Music"] = "الموسيقى",
        ["Volume"] = "مستوى الصوت",
        ["Master Volume"] = "مستوى الصوت العام",
        ["Music Volume"] = "مستوى صوت الموسيقى",
        ["Sound Effects"] = "المؤثرات الصوتية",
        ["Dialogue Volume"] = "مستوى صوت الحوار",
        ["Mute"] = "كتم الصوت",
        ["Controls"] = "التحكم",
        ["Control Settings"] = "إعدادات التحكم",
        ["Keyboard"] = "لوحة المفاتيح",
        ["Mouse"] = "الفأرة",
        ["Controller"] = "يد التحكم",
        ["Key Bindings"] = "تعيين الأزرار",
        ["Mouse Sensitivity"] = "حساسية الفأرة",
        ["Invert Y Axis"] = "عكس المحور الرأسي",
        ["Language"] = "اللغة",
        ["Text Language"] = "لغة النص",
        ["Voice Language"] = "لغة الأصوات",
        ["Subtitles"] = "الترجمة النصية",
        ["Subtitle Language"] = "لغة الترجمة النصية",
        ["Accessibility"] = "إمكانية الوصول",
        ["Inventory"] = "المخزون",
        ["Map"] = "الخريطة",
        ["Quest"] = "مهمة",
        ["Quests"] = "المهام",
        ["Objectives"] = "الأهداف",
        ["Skills"] = "المهارات",
        ["Equipment"] = "المعدات",
        ["Weapons"] = "الأسلحة",
        ["Achievements"] = "الإنجازات",
        ["Profile"] = "الملف الشخصي",
        ["Help"] = "المساعدة",
        ["About"] = "حول البرنامج",
        ["Home"] = "الرئيسية",
        ["Search"] = "بحث",
        ["Install"] = "تثبيت",
        ["Uninstall"] = "إزالة التثبيت",
        ["Update"] = "تحديث",
        ["Download"] = "تنزيل",
        ["Downloads"] = "التنزيلات",
        ["This PC"] = "هذا الكمبيوتر",
        ["My Computer"] = "جهاز الكمبيوتر",
        ["Recycle Bin"] = "سلة المحذوفات",
        ["Control Panel"] = "لوحة التحكم",
        ["PC Health Check"] = "فحص صحة الكمبيوتر",
        ["Task Manager"] = "مدير المهام",
        ["File Explorer"] = "مستكشف الملفات",
        ["Desktop"] = "سطح المكتب",
        ["Documents"] = "المستندات",
        ["Pictures"] = "الصور",
        ["Videos"] = "مقاطع الفيديو",
        ["Properties"] = "خصائص",
        ["Rename"] = "إعادة تسمية",
        ["Copy"] = "نسخ",
        ["Paste"] = "لصق",
        ["Cut"] = "قص",
        ["Tor Browser"] = "متصفح Tor",
        ["NVIDIA App"] = "تطبيق NVIDIA",
        ["NVIDIA Control Panel"] = "لوحة تحكم NVIDIA"
    };

    private static readonly HashSet<string> Products = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steam", "Brave", "Discord", "Wand", "Wand (WeMod)", "WeMod", "EA", "VALORANT",
        "Google Chrome", "Microsoft Edge", "Visual Studio Code", "Ubisoft Connect", "Riot Client",
        "Riot Client Engine", "Epic Games Launcher", "Revo Uninstaller", "Free Download Manager",
        "DefenderUI", "Driver Booster", "Driver Booster Free", "WinDirStat", "VLC media player",
        "Antigravity", "Antigravity IDE", "FACEIT AC", "CMake", "CMake (cmake-gui)", "OBS Studio"
    };

    private static readonly string[] ProductPrefixes =
    ["Cortex", "NVIDIA", "Kingston", "Redragon", "FurMark", "Boosteroid", "Microsoft", "Google", "Adobe"];

    public static string Normalize(string text)
    {
        var normalized = Regex.Replace(text.Normalize(NormalizationForm.FormKC), @"\s+", " ").Trim();
        return Regex.Replace(normalized, @"(^|\s)&(?=\p{L})", "$1");
    }

    public static bool IsKnownPhrase(string text)
    {
        var normalized = Normalize(text);
        return Terms.ContainsKey(normalized) || Products.Contains(normalized)
            || (normalized.EndsWith("...", StringComparison.Ordinal) && normalized.Length >= 7
                && Products.Any(product => product.StartsWith(normalized[..^3], StringComparison.OrdinalIgnoreCase)));
    }

    public static bool TryTranslate(string text, out string translated)
    {
        var normalized = Normalize(text);
        var suffix = string.Empty;
        var shortcut = Regex.Match(normalized, @"\s*[\(\[](?:F\d{1,2}|(?:(?:Ctrl|Alt|Shift)\+)+[A-Z0-9]+)[\)\]]$", RegexOptions.IgnoreCase);
        if (shortcut.Success)
        {
            suffix = shortcut.Value;
            normalized = normalized[..shortcut.Index].TrimEnd();
        }

        if (normalized.EndsWith("...", StringComparison.Ordinal))
        {
            suffix = "..." + suffix;
            normalized = normalized[..^3].TrimEnd();
        }

        if (TryDirect(normalized, out translated))
        {
            translated += suffix;
            return true;
        }

        var colon = normalized.IndexOf(':');
        if (colon > 0 && Terms.TryGetValue(normalized[..colon].Trim(), out var label))
        {
            var value = normalized[(colon + 1)..].Trim();
            if (TryDirect(value, out var translatedValue) || value.Length == 0)
            {
                translated = label + ":" + (value.Length == 0 ? string.Empty : " " + translatedValue) + suffix;
                return true;
            }
        }

        // OCR cannot reconstruct the hidden part of an ellipsized name.
        // Preserve an unknown truncated label instead of inventing a meaning.
        if (suffix.StartsWith("...", StringComparison.Ordinal))
        {
            translated = normalized + suffix;
            return true;
        }

        translated = string.Empty;
        return false;
    }

    private static bool TryDirect(string text, out string translated)
    {
        if (Terms.TryGetValue(text, out translated!))
        {
            return true;
        }

        if (Products.Contains(text)
            || ProductPrefixes.Any(prefix => text.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || text.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
            || Regex.IsMatch(text, @"^[A-Z0-9][A-Z0-9 ._+/\-]{0,39}$")
            || Regex.IsMatch(text, @"^(?=.*[A-Za-z])(?=.*\d)[A-Za-z0-9._+\-]+$")
            || Regex.IsMatch(text, @"^\d+(?:[.,x×]\d+)*\s*%?$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(text, @"\.(?:exe|lnk|txt|pdf|docx?|xlsx?|pptx?|zip|rar|7z|png|jpe?g|mp[34])$", RegexOptions.IgnoreCase))
        {
            translated = text;
            return true;
        }

        translated = string.Empty;
        return false;
    }
}
