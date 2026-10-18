using Andzip.Core.Lang;

namespace Andzip.Core.Tests;

/// <summary>语言包键覆盖率：确保 UI（尤其是“新建压缩文件”对话框）使用的键在两种语言中都存在，避免界面回退显示键名。</summary>
public class LangKeyCoverageTests
{
    private static readonly string[] DialogKeys =
    [
        "TEXT_TITLE_NEW_ARCHIVE",
        "TEXT_SECTION_ADD_FILES",
        "TEXT_SECTION_ARCHIVE_SETTINGS",
        "TEXT_COLUMN_NAME",
        "TEXT_COLUMN_SIZE",
        "TEXT_COLUMN_FILE_PATH",
        "TEXT_BTN_ADD",
        "TEXT_BTN_DELETE",
        "TEXT_MENU_ADD_FILES",
        "TEXT_MENU_ADD_FOLDER",
        "TEXT_LABEL_FILE_NAME",
        "TEXT_BTN_BROWSE",
        "TEXT_LABEL_FORMAT",
        "TEXT_BTN_SET_PASSWORD",
        "TEXT_LABEL_VOLUME",
        "TEXT_VOLUME_NONE",
        "TEXT_VOLUME_3MB",
        "TEXT_VOLUME_4MB",
        "TEXT_VOLUME_5MB",
        "TEXT_VOLUME_10MB",
        "TEXT_VOLUME_20MB",
        "TEXT_VOLUME_25MB_GMAIL",
        "TEXT_VOLUME_100MB",
        "TEXT_VOLUME_300MB",
        "TEXT_VOLUME_650MB_CD",
        "TEXT_VOLUME_700MB_CD",
        "TEXT_VOLUME_4GB_FAT32",
        "TEXT_VOLUME_47GB_DVD",
        "TEXT_VOLUME_CUSTOM",
        "TEXT_TITLE_VOLUME_SIZE",
        "TEXT_LABEL_VOLUME_SIZE",
        "TEXT_ERR_VOLUME_SIZE_INVALID",
        "TEXT_LABEL_COMPRESSION_LEVEL",
        "TEXT_BTN_ADVANCED_SETTINGS",
        "TEXT_BTN_ADVANCED_SETTINGS_COLLAPSE",
        "TEXT_ADVANCED_TITLE",
        "TEXT_LABEL_COMPRESSION_METHOD",
        "TEXT_METHOD_EXTENSION_NOTE",
        "TEXT_METHOD_STORE",
        "TEXT_METHOD_DEFLATE",
        "TEXT_LABEL_ENCRYPTION_METHOD",
        "TEXT_ENCRYPTIONMETHOD_ZIPCRYPTO",
        "TEXT_ENCRYPTIONMETHOD_AES256",
        "TEXT_LABEL_CPU_THREADS",
        "TEXT_THREADS_AUTO",
        "TEXT_CHECK_STORE_SYMLINKS",
        "TEXT_CHECK_FILE_COMMENT",
        "TEXT_CHECK_TEST_AFTER_COMPRESSION",
        "TEXT_CHECK_DELETE_SOURCE",
        "TEXT_CHECK_SEPARATE_ARCHIVES",
        "TEXT_CHECK_MORE_OPTIONS",
        "TEXT_BTN_START",
        "TEXT_BTN_CANCEL",
        "TEXT_DEFAULT_ARCHIVE_NAME",
        "TEXT_STATUS_COMPRESSING",
        "TEXT_STATUS_NOT_IMPLEMENTED",
        "TEXT_ERR_NO_INPUT",
        "TEXT_ERR_OUTPUT_NOT_SET",
        "TEXT_ERR_GZ_SINGLE_FILE",
        "TEXT_ERR_PASSWORD_ASCII",
        "TEXT_COMPRESSIONMETHOD_STORE",
        "TEXT_COMPRESSIONMETHOD_FAST",
        "TEXT_COMPRESSIONMETHOD_NORMAL",
        "TEXT_COMPRESSIONMETHOD_MAX",
        "TEXT_TITLE_PASSWORD_INPUT",
        "TEXT_LABEL_PASSWORD",
        "TEXT_LABEL_PASSWORD_CONFIRM",
        "TEXT_ERR_PASSWORD_MISMATCH",
        "TEXT_BTN_OK",
        "TEXT_TITLE_EXTRACT_PATH",
        "TEXT_LABEL_DEST_PATH",
        "TEXT_SECTION_EXTRACT",
        "TEXT_RADIO_ALL_FILES",
        "TEXT_RADIO_SELECTED_FILES",
        "TEXT_BTN_NEW_FOLDER",
        "TEXT_CHECK_EXTRACT_TO_SUBFOLDER",
        "TEXT_CHECK_DELETE_ARCHIVE_AFTER",
        "TEXT_CHECK_REMEMBER_SETTINGS",
        "TEXT_NEW_FOLDER_DEFAULT",
        "TEXT_STATUS_EXTRACTING",
        "TEXT_ERR_DEST_NOT_SET",
        "TEXT_TITLE_ERROR",
        "TEXT_ERR_EXTRACT_FAILED",
        "TEXT_TITLE_ASK_DUPEFILE",
        "TEXT_MSG_FILES_EXIST",
        "TEXT_BTN_RENAME_ALL",
        "TEXT_BTN_OVERWRITE_ALL",
        "TEXT_BTN_SKIP_ALL",
        "TEXT_ERR_PASSWORD_EMPTY",
        "TEXT_COLUMN_COMPRESSED_SIZE",
        "TEXT_COLUMN_ORIGINAL_SIZE",
        "TEXT_COLUMN_TYPE",
        "TEXT_TYPE_FOLDER",
        "TEXT_TYPE_FILE",
        "TEXT_ARCHIVE_FILES",
        "TEXT_STATUS_ARCHIVE_SUMMARY",
        "TEXT_STATUS_SELECTED_SUMMARY",
        "TEXT_ERR_OPEN_ARCHIVE_FAILED",
        "TEXT_ERR_OPEN_ARCHIVE_NOT_SUPPORTED",
        "TEXT_TOOLBAR_OPEN",
        "TEXT_TOOLBAR_EXTRACT",
        "TEXT_TOOLBAR_NEW",
        "TEXT_TOOLBAR_ADD",
        "TEXT_TOOLBAR_DELETE",
        "TEXT_TOOLBAR_TEST",
        "TEXT_TOOLBAR_CRC",
        "TEXT_TOOLBAR_VIEW",
        "TEXT_COLUMN_MODIFIED",
        "TEXT_COLUMN_CRC",
        "TEXT_COLUMN_ATTRIBUTES",
        "TEXT_COLUMN_COMMENT",
        "TEXT_COLUMN_EXTRA",
        "TEXT_TYPE_FILE_EXT",
        "TEXT_ENCRYPTED_FLAG",
        "TEXT_EXTRA_OS_WINDOWS",
        "TEXT_EXTRA_OS_UNIX",
        "TEXT_EXTRA_UTF8",
        "TEXT_VIEW_DETAILS",
        "TEXT_VIEW_ICONS",
        "TEXT_OPEN_ARCHIVE_LOCATION",
        "TEXT_TITLE_SETTINGS",
        "TEXT_SETTINGS_GENERAL",
        "TEXT_SETTINGS_FILE_ASSOCIATION",
        "TEXT_SETTINGS_CONTEXT_MENU",
        "TEXT_SETTINGS_EXTRACT",
        "TEXT_SETTINGS_COMPRESS",
        "TEXT_SETTINGS_VIEW",
        "TEXT_SETTINGS_SECURITY",
        "TEXT_SETTINGS_ADVANCED",
        "TEXT_SETTINGS_LANGUAGE",
        "TEXT_BTN_RESET",
        "TEXT_BTN_APPLY",
        "TEXT_BTN_SELECT_ALL",
        "TEXT_BTN_DESELECT_ALL",
        "TEXT_SETTINGS_WINDOWS_DEFAULT_APPS",
        "TEXT_SETTINGS_GENERAL_TITLE",
        "TEXT_SETTINGS_DOUBLE_CLICK_LABEL",
        "TEXT_SETTINGS_DOUBLE_CLICK_OPEN",
        "TEXT_SETTINGS_DOUBLE_CLICK_EXTRACT",
        "TEXT_SETTINGS_DOUBLE_CLICK_HINT",
        "TEXT_SETTINGS_ESC_CANCEL",
        "TEXT_SETTINGS_NOT_IMPLEMENTED",
        "TEXT_SETTINGS_ASSOC_HINT",
        "TEXT_SETTINGS_ASSOC_UNSUPPORTED",
        "TEXT_SETTINGS_ASSOC_APPLY_FAILED",
        "TEXT_GROUP_ARCHIVE",
        "TEXT_GROUP_UNIX",
        "TEXT_GROUP_OTHER",
        "TEXT_GROUP_CD_DVD",
        "TEXT_MENU_OPEN_WITH",
        "MENUSTR_RECENT",
        "MENUSTR_RECENT_EMPTY",
        "MENUSTR_CLEAR_RECENT",
        "TEXT_MENU_EXTRACT",
        "TEXT_MENU_EXTRACT_DELETE",
        "TEXT_MENU_COMPRESS_ZIP",
        "TEXT_MENU_COMPRESS_DIALOG",
        "TEXT_SETTINGS_CONTEXT_MENU_TITLE",
        "TEXT_SETTINGS_CTX_NOTE",
        "TEXT_SETTINGS_CTX_COMPRESS_FILES",
        "TEXT_SETTINGS_CTX_COMPRESS_FOLDERS",
        "TEXT_SETTINGS_CTX_COMPRESS_HINT",
        "TEXT_SETTINGS_CTX_EXTRACT",
        "TEXT_SETTINGS_CTX_EXTRACT_HINT",
        "TEXT_SETTINGS_CTX_EXTRACT_RULES",
        "TEXT_SETTINGS_CTX_FAILED",
        "TEXT_BTN_SET_DEFAULT",
        "TEXT_SETTINGS_DEFAULT_STILL",
        "TEXT_ERR_COMPRESS_FAILED",
        "TEXT_ERR_TARGET_LOCKED",

        // 完整性检测与修复：都是"跑完要给一句话"的操作，漏一个键就是界面上一串键名。
        "TEXT_STATUS_TESTING",
        "TEXT_TITLE_TEST_PASSED",
        "TEXT_TEST_PASSED",
        "TEXT_TITLE_TEST_FAILED",
        "TEXT_TEST_NOT_VERIFIED",
        "TEXT_TITLE_REPAIR",
        "TEXT_REPAIR_INTRO",
        "TEXT_REPAIR_MODE_1",
        "TEXT_REPAIR_MODE_1_HINT",
        "TEXT_REPAIR_MODE_2",
        "TEXT_REPAIR_MODE_2_HINT",
        "TEXT_REPAIR_MODE_3",
        "TEXT_REPAIR_MODE_3_HINT",
        "TEXT_REPAIR_SOURCE",
        "TEXT_REPAIR_OUTPUT",
        "TEXT_REPAIR_START",
        "TEXT_STATUS_REPAIRING",
        "TEXT_ERR_REPAIR_FAILED",
        "TEXT_TITLE_REPAIR_RESULT",
        "TEXT_REPAIR_RESULT",
        "TEXT_REPAIR_NOTHING",
        "TEXT_REPAIR_RECOVERED",
        "TEXT_REPAIR_SKIPPED",
    ];

    [Theory]
    [InlineData("SimpChinese")]
    [InlineData("English")]
    public void NewArchiveDialogKeys_ExistInBothLanguages(string language)
    {
        var catalog = LangLoader.CreateCatalog(typeof(LangKeyCoverageTests).Assembly, AppContext.BaseDirectory);
        Assert.True(catalog.SetLanguage(language), $"缺少语言 {language}");

        foreach (var key in DialogKeys)
        {
            var value = catalog.Get(key);
            Assert.False(value == key, $"[{language}] 缺少语言键 {key}");
            Assert.False(string.IsNullOrWhiteSpace(value), $"[{language}] 键 {key} 的文案为空");
        }
    }

    [Fact]
    public void MenuKeys_ExistInBothLanguages()
    {
        var catalog = LangLoader.CreateCatalog(typeof(LangKeyCoverageTests).Assembly, AppContext.BaseDirectory);
        var menuKeys = Andzip.Core.Menu.MainMenuModel.Create()
            .SelectMany(m => new[] { m.TextKey }.Concat(m.Items.Where(i => !i.IsSeparator).Select(i => i.TextKey)))
            .Distinct();

        foreach (var language in new[] { "SimpChinese", "English" })
        {
            Assert.True(catalog.SetLanguage(language));

            foreach (var key in menuKeys)
            {
                Assert.False(catalog.Get(key) == key, $"[{language}] 缺少菜单键 {key}");
            }
        }
    }
}