using System.Globalization;

namespace DoorMan;

internal enum Language { English, Spanish, French, Chinese, Hindi }

/// <summary>
/// Every text the user can see, in the languages DoorMan supports. The language is the Windows display language
/// of the current user; any other language gets English. Each entry lists its translations in the order of
/// <see cref="Language"/>: English, Spanish, French, Chinese (Simplified) and Hindi.
/// </summary>
internal static class Strings
{
    /// <summary>Settable so that tests can check every language.</summary>
    internal static Language Current { get; set; } = FromCulture(CultureInfo.CurrentUICulture);

    internal static Language FromCulture(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "es" => Language.Spanish,
        "fr" => Language.French,
        "zh" => Language.Chinese,
        "hi" => Language.Hindi,
        _ => Language.English,
    };

    static string T(string en, string es, string fr, string zh, string hi) => Current switch
    {
        Language.Spanish => es,
        Language.French => fr,
        Language.Chinese => zh,
        Language.Hindi => hi,
        _ => en,
    };

    static string F(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    // Context menu and file types.

    public static string ProtectWithPassword => T(
        "Protect with password", "Proteger con contraseña", "Protéger par mot de passe", "使用密码保护", "पासवर्ड से सुरक्षित करें");

    public static string LockVerb => T("Lock", "Bloquear", "Verrouiller", "锁定", "लॉक करें");

    public static string UnlockVerb => T("Unlock", "Desbloquear", "Déverrouiller", "解锁", "अनलॉक करें");

    public static string ProtectedFolderType => T(
        "Password-protected folder", "Carpeta protegida con contraseña", "Dossier protégé par mot de passe",
        "受密码保护的文件夹", "पासवर्ड से सुरक्षित फ़ोल्डर");

    public static string OpenProtectedFolder => T(
        "Open protected folder", "Abrir carpeta protegida", "Ouvrir le dossier protégé", "打开受保护的文件夹", "सुरक्षित फ़ोल्डर खोलें");

    // Locking and unlocking.

    public static string AlreadyProtectedTitle => T(
        "This folder is already protected", "Esta carpeta ya está protegida", "Ce dossier est déjà protégé",
        "此文件夹已受保护", "यह फ़ोल्डर पहले से सुरक्षित है");

    public static string AlreadyLockedTitle => T(
        "This folder is already locked", "Esta carpeta ya está bloqueada", "Ce dossier est déjà verrouillé",
        "此文件夹已锁定", "यह फ़ोल्डर पहले से लॉक है");

    public static string DoubleClickToOpen => T(
        "Double-click it to enter its password.", "Ábrela con doble clic para escribir su contraseña.",
        "Double-cliquez dessus pour saisir son mot de passe.", "双击它即可输入密码。",
        "पासवर्ड डालने के लिए उस पर डबल-क्लिक करें।");

    public static string Encrypting(string folder) => F(T(
        "Encrypting “{0}”…", "Cifrando «{0}»…", "Chiffrement de « {0} »…", "正在加密“{0}”…", "“{0}” एन्क्रिप्ट किया जा रहा है…"), folder);

    public static string Decrypting(string folder) => F(T(
        "Decrypting “{0}”…", "Descifrando «{0}»…", "Déchiffrement de « {0} »…", "正在解密“{0}”…", "“{0}” डिक्रिप्ट किया जा रहा है…"), folder);

    public static string PreviousPasswordNotFound => T(
        "The previous password of this folder was not found. Choose a password to lock it again.",
        "No se ha encontrado la contraseña anterior de esta carpeta. Elige una contraseña para volver a bloquearla.",
        "Le mot de passe précédent de ce dossier est introuvable. Choisissez un mot de passe pour le verrouiller à nouveau.",
        "找不到此文件夹之前的密码。请设置一个密码以重新锁定它。",
        "इस फ़ोल्डर का पिछला पासवर्ड नहीं मिला। इसे फिर से लॉक करने के लिए एक पासवर्ड चुनें।");

    public static string NotDeletedTitle => T(
        "Some items were not removed", "Quedan elementos sin eliminar", "Certains éléments n’ont pas été supprimés",
        "部分项目未被删除", "कुछ आइटम हटाए नहीं गए");

    public static string NotDeletedText(string folder, string items) => F(T(
        "“{0}” is encrypted, but these items were in use and were not removed: {1}. Close them and delete them manually.",
        "«{0}» está cifrada, pero estos elementos estaban en uso y no se eliminaron: {1}. Ciérralos y elimínalos a mano.",
        "« {0} » est chiffré, mais ces éléments étaient en cours d’utilisation et n’ont pas été supprimés : {1}. Fermez-les et supprimez-les manuellement.",
        "“{0}”已加密，但以下项目正在使用中，未被删除：{1}。请关闭后手动删除。",
        "“{0}” एन्क्रिप्ट हो गया है, लेकिन ये आइटम उपयोग में थे और हटाए नहीं गए: {1}। इन्हें बंद करके स्वयं हटाएँ।"), folder, items);

    public static string OpenFilesTitle => T(
        "Some files are open", "Hay archivos abiertos", "Des fichiers sont ouverts", "有文件处于打开状态", "कुछ फ़ाइलें खुली हैं");

    public static string OpenFilesText(string folder, string items) => F(T(
        "“{0}” is encrypted, but these files are open: {1}. Their content will be removed as soon as you close them.",
        "«{0}» está cifrada, pero estos archivos están abiertos: {1}. Su contenido se quitará en cuanto los cierres.",
        "« {0} » est chiffré, mais ces fichiers sont ouverts : {1}. Leur contenu sera retiré dès que vous les fermerez.",
        "“{0}”已加密，但以下文件仍处于打开状态：{1}。关闭后其内容将被移除。",
        "“{0}” एन्क्रिप्ट हो गया है, लेकिन ये फ़ाइलें खुली हैं: {1}। इन्हें बंद करते ही इनकी सामग्री हटा दी जाएगी।"), folder, items);

    /// <summary>A file or folder name between the quotation marks of the language.</summary>
    public static string Quoted(string name) => F(T("“{0}”", "«{0}»", "« {0} »", "“{0}”", "“{0}”"), name);

    public static string AndMore(string first, int more) => F(T(
        "{0} and {1} more", "{0} y {1} más", "{0} et {1} autre(s)", "{0}及另外 {1} 项", "{0} और {1} अन्य"), first, more);

    // Windows 11 context menu.

    public static string ContextMenuTitle => T(
        "Windows 11 context menu", "Menú contextual de Windows 11", "Menu contextuel de Windows 11",
        "Windows 11 上下文菜单", "Windows 11 संदर्भ मेनू");

    public static string PackageUacDeclined => T(
        "Administrator permission was not granted: DoorMan’s options will stay under “Show more options”.",
        "No se concedieron permisos de administrador: las opciones de DoorMan seguirán en «Mostrar más opciones».",
        "L’autorisation d’administrateur n’a pas été accordée : les options de DoorMan resteront dans « Afficher d’autres options ».",
        "未授予管理员权限：DoorMan 的选项将保留在“显示更多选项”中。",
        "व्यवस्थापक अनुमति नहीं दी गई: DoorMan के विकल्प “अधिक विकल्प दिखाएँ” में रहेंगे।");

    public static string PackageCertificateFailed => T(
        "DoorMan’s certificate could not be added: the options will stay under “Show more options”.",
        "No se pudo añadir el certificado de DoorMan: las opciones seguirán en «Mostrar más opciones».",
        "Le certificat de DoorMan n’a pas pu être ajouté : les options resteront dans « Afficher d’autres options ».",
        "无法添加 DoorMan 的证书：选项将保留在“显示更多选项”中。",
        "DoorMan का प्रमाणपत्र नहीं जोड़ा जा सका: विकल्प “अधिक विकल्प दिखाएँ” में रहेंगे।");

    public static string PackageFailed(string error) => F(T(
        "The Windows 11 context menu could not be registered: {0}",
        "No se pudo registrar el menú contextual de Windows 11: {0}",
        "Le menu contextuel de Windows 11 n’a pas pu être enregistré : {0}",
        "无法注册 Windows 11 上下文菜单：{0}",
        "Windows 11 संदर्भ मेनू पंजीकृत नहीं किया जा सका: {0}"), error);

    // Errors while locking, unlocking and checking folders.

    public static string FolderDoesNotExist => T(
        "The folder does not exist.", "La carpeta no existe.", "Le dossier n’existe pas.", "该文件夹不存在。", "फ़ोल्डर मौजूद नहीं है।");

    public static string FolderAlreadyProtected => T(
        "The folder is already protected.", "La carpeta ya está protegida.", "Le dossier est déjà protégé.",
        "该文件夹已受保护。", "फ़ोल्डर पहले से सुरक्षित है।");

    public static string CheckingEncryptedFile => T(
        "Checking the encrypted file…", "Comprobando el archivo cifrado…", "Vérification du fichier chiffré…",
        "正在检查加密文件…", "एन्क्रिप्ट की गई फ़ाइल जाँची जा रही है…");

    public static string RemovingUnencryptedFiles => T(
        "Removing the unencrypted files…", "Eliminando los archivos sin cifrar…", "Suppression des fichiers non chiffrés…",
        "正在删除未加密的文件…", "बिना एन्क्रिप्ट की फ़ाइलें हटाई जा रही हैं…");

    public static string SettingsUnavailable => T(
        "DoorMan’s settings could not be accessed.", "No se pudo acceder a la configuración de DoorMan.",
        "Impossible d’accéder aux paramètres de DoorMan.", "无法访问 DoorMan 的设置。", "DoorMan की सेटिंग्स तक पहुँचा नहीं जा सका।");

    public static string FolderUnavailable => T(
        "The folder does not exist or is not available.", "La carpeta no existe o no está disponible.",
        "Le dossier n’existe pas ou n’est pas disponible.", "该文件夹不存在或不可用。", "फ़ोल्डर मौजूद नहीं है या उपलब्ध नहीं है।");

    public static string CannotProtectLinks => T(
        "Symbolic links and junctions cannot be protected.", "No se pueden proteger enlaces simbólicos ni puntos de unión.",
        "Les liens symboliques et les jonctions ne peuvent pas être protégés.", "无法保护符号链接或交接点。",
        "सिम्बॉलिक लिंक और जंक्शन सुरक्षित नहीं किए जा सकते।");

    public static string CannotProtectDriveRoot => T(
        "The root of a drive cannot be protected.", "No se puede proteger la raíz de una unidad.",
        "La racine d’un lecteur ne peut pas être protégée.", "无法保护驱动器的根目录。", "किसी ड्राइव का रूट सुरक्षित नहीं किया जा सकता।");

    public static string CannotProtectSystemFolders => T(
        "System folders and program settings folders cannot be protected.",
        "No se pueden proteger carpetas del sistema ni de configuración de programas.",
        "Les dossiers système et les dossiers de configuration des programmes ne peuvent pas être protégés.",
        "无法保护系统文件夹或程序配置文件夹。",
        "सिस्टम फ़ोल्डर और प्रोग्राम सेटिंग फ़ोल्डर सुरक्षित नहीं किए जा सकते।");

    public static string CannotProtectInstallFolder => T(
        "The folder where DoorMan is installed cannot be protected.",
        "No se puede proteger la carpeta donde está instalado DoorMan.",
        "Le dossier où DoorMan est installé ne peut pas être protégé.",
        "无法保护 DoorMan 的安装文件夹。",
        "जिस फ़ोल्डर में DoorMan इंस्टॉल है, उसे सुरक्षित नहीं किया जा सकता।");

    public static string CannotProtectContainsSystem => T(
        "This folder cannot be protected because it contains system folders or your user profile.",
        "No se puede proteger esta carpeta porque contiene carpetas del sistema o tu perfil de usuario.",
        "Ce dossier ne peut pas être protégé, car il contient des dossiers système ou votre profil utilisateur.",
        "无法保护此文件夹，因为其中包含系统文件夹或你的用户配置文件。",
        "यह फ़ोल्डर सुरक्षित नहीं किया जा सकता क्योंकि इसमें सिस्टम फ़ोल्डर या आपकी उपयोगकर्ता प्रोफ़ाइल है।");

    public static string CannotProtectPersonalFolder => T(
        "A whole Windows personal folder cannot be protected. Protect a subfolder instead.",
        "No se puede proteger una carpeta personal de Windows completa. Protege una subcarpeta en su lugar.",
        "Un dossier personnel Windows complet ne peut pas être protégé. Protégez plutôt un sous-dossier.",
        "无法保护整个 Windows 个人文件夹。请改为保护其中的子文件夹。",
        "पूरा Windows व्यक्तिगत फ़ोल्डर सुरक्षित नहीं किया जा सकता। इसके बजाय कोई सबफ़ोल्डर सुरक्षित करें।");

    public static string NoWritePermission => T(
        "You do not have permission to write to this folder.", "No tienes permiso de escritura en esta carpeta.",
        "Vous n’avez pas l’autorisation d’écrire dans ce dossier.", "你没有此文件夹的写入权限。",
        "आपके पास इस फ़ोल्डर में लिखने की अनुमति नहीं है।");

    public static string CannotWrite(string error) => F(T(
        "Cannot write to this folder: {0}", "No se puede escribir en esta carpeta: {0}", "Impossible d’écrire dans ce dossier : {0}",
        "无法写入此文件夹：{0}", "इस फ़ोल्डर में लिखा नहीं जा सकता: {0}"), error);

    public static string UnsupportedItem(string item) => F(T(
        "The folder contains “{0}”, which is a symbolic link, a junction or a cloud file that has not been downloaded. Items of this kind cannot be protected.",
        "La carpeta contiene «{0}», que es un enlace simbólico, un punto de unión o un archivo en la nube sin descargar. Este tipo de elementos no se puede proteger.",
        "Le dossier contient « {0} », qui est un lien symbolique, une jonction ou un fichier cloud non téléchargé. Ce type d’élément ne peut pas être protégé.",
        "该文件夹包含“{0}”，它是符号链接、交接点或尚未下载的云文件。此类项目无法保护。",
        "फ़ोल्डर में “{0}” है, जो एक सिम्बॉलिक लिंक, जंक्शन या बिना डाउनलोड की गई क्लाउड फ़ाइल है। इस प्रकार के आइटम सुरक्षित नहीं किए जा सकते।"), item);

    public static string FileChanged(string file) => F(T(
        "The file “{0}” changed while it was being encrypted. Close it and try again.",
        "El archivo «{0}» cambió mientras se cifraba. Ciérralo e inténtalo de nuevo.",
        "Le fichier « {0} » a été modifié pendant son chiffrement. Fermez-le et réessayez.",
        "文件“{0}”在加密过程中发生了更改。请关闭它后重试。",
        "फ़ाइल “{0}” एन्क्रिप्ट होते समय बदल गई। इसे बंद करके फिर से प्रयास करें।"), file);

    public static string VaultDamaged => T(
        "The folder’s encrypted file is incomplete or damaged.",
        "El archivo cifrado de la carpeta está incompleto o dañado.",
        "Le fichier chiffré du dossier est incomplet ou endommagé.",
        "该文件夹的加密文件不完整或已损坏。",
        "फ़ोल्डर की एन्क्रिप्ट की गई फ़ाइल अधूरी या क्षतिग्रस्त है।");

    public static string VaultInvalid => T(
        "The protected folder’s file is not valid or is damaged.",
        "El archivo de la carpeta protegida no es válido o está dañado.",
        "Le fichier du dossier protégé n’est pas valide ou est endommagé.",
        "受保护文件夹的文件无效或已损坏。",
        "सुरक्षित फ़ोल्डर की फ़ाइल अमान्य या क्षतिग्रस्त है।");

    public static string VerifyFailed => T(
        "Checking the encrypted file failed. No file in the folder was changed.",
        "La comprobación del archivo cifrado ha fallado. No se ha modificado ningún archivo de la carpeta.",
        "La vérification du fichier chiffré a échoué. Aucun fichier du dossier n’a été modifié.",
        "加密文件校验失败。文件夹中的文件均未被修改。",
        "एन्क्रिप्ट की गई फ़ाइल की जाँच विफल रही। फ़ोल्डर की कोई फ़ाइल नहीं बदली गई।");

    public static string CannotAccess(string item) => F(T(
        "Cannot access “{0}”.", "No se puede acceder a «{0}».", "Impossible d’accéder à « {0} ».", "无法访问“{0}”。",
        "“{0}” तक पहुँचा नहीं जा सकता।"), item);

    public static string WrongPassword => T(
        "The password is not correct.", "La contraseña no es correcta.", "Le mot de passe est incorrect.", "密码不正确。",
        "पासवर्ड सही नहीं है।");

    // Dialogs.

    public static string OK => T("OK", "Aceptar", "OK", "确定", "ठीक है");

    public static string Cancel => T("Cancel", "Cancelar", "Annuler", "取消", "रद्द करें");

    public static string Close => T("Close", "Cerrar", "Fermer", "关闭", "बंद करें");

    public static string MainHeading => T(
        "Protect folders with a password", "Protege carpetas con contraseña", "Protégez vos dossiers par mot de passe",
        "使用密码保护文件夹", "फ़ोल्डरों को पासवर्ड से सुरक्षित करें");

    public static string MainHelp => F(T(
        "In File Explorer, right-click a folder and choose {0}.",
        "En el Explorador de archivos, haz clic derecho en una carpeta y elige {0}.",
        "Dans l’Explorateur de fichiers, cliquez avec le bouton droit sur un dossier et choisissez {0}.",
        "在文件资源管理器中，右键单击文件夹并选择{0}。",
        "फ़ाइल एक्सप्लोरर में किसी फ़ोल्डर पर राइट-क्लिक करें और {0} चुनें।"), Quoted(ProtectWithPassword));

    public static string ProtectedFoldersLabel => T(
        "Protected folders:", "Carpetas protegidas:", "Dossiers protégés :", "受保护的文件夹：", "सुरक्षित फ़ोल्डर:");

    public static string FolderColumn => T("Folder", "Carpeta", "Dossier", "文件夹", "फ़ोल्डर");

    public static string StatusColumn => T("Status", "Estado", "État", "状态", "स्थिति");

    public static string Locked => T("Locked", "Bloqueada", "Verrouillé", "已锁定", "लॉक");

    public static string Unlocked => T("Unlocked", "Desbloqueada", "Déverrouillé", "已解锁", "अनलॉक");

    public static string RemoveFromExplorerQuestion => T(
        "Remove DoorMan from File Explorer?", "¿Quitar DoorMan del Explorador?", "Retirer DoorMan de l’Explorateur de fichiers ?",
        "要从文件资源管理器中移除 DoorMan 吗？", "DoorMan को फ़ाइल एक्सप्लोरर से हटाएँ?");

    public static string RemoveFromExplorerText => T(
        "Locked folders will stay encrypted. To open them you will have to turn the integration back on.",
        "Las carpetas bloqueadas seguirán cifradas. Para abrirlas tendrás que volver a activar la integración.",
        "Les dossiers verrouillés resteront chiffrés. Pour les ouvrir, vous devrez réactiver l’intégration.",
        "已锁定的文件夹将保持加密。要打开它们，需要重新启用集成。",
        "लॉक किए गए फ़ोल्डर एन्क्रिप्टेड रहेंगे। उन्हें खोलने के लिए आपको एकीकरण फिर से चालू करना होगा।");

    public static string Remove => T("Remove", "Quitar", "Retirer", "移除", "हटाएँ");

    public static string IntegrationOn => T(
        "File Explorer integration is on.", "La integración con el Explorador está activada.",
        "L’intégration à l’Explorateur de fichiers est activée.", "文件资源管理器集成已启用。", "फ़ाइल एक्सप्लोरर एकीकरण चालू है।");

    public static string IntegrationOff => T(
        "File Explorer integration is off. Turn it on to see the options in the context menu.",
        "La integración con el Explorador está desactivada. Actívala para ver las opciones en el menú contextual.",
        "L’intégration à l’Explorateur de fichiers est désactivée. Activez-la pour voir les options dans le menu contextuel.",
        "文件资源管理器集成已禁用。启用后即可在上下文菜单中看到相关选项。",
        "फ़ाइल एक्सप्लोरर एकीकरण बंद है। संदर्भ मेनू में विकल्प देखने के लिए इसे चालू करें।");

    public static string RemoveFromExplorer => T(
        "Remove from File Explorer", "Quitar del Explorador", "Retirer de l’Explorateur", "从资源管理器中移除", "एक्सप्लोरर से हटाएँ");

    public static string AddToExplorer => T(
        "Turn on in File Explorer", "Activar en el Explorador", "Activer dans l’Explorateur", "在资源管理器中启用", "एक्सप्लोरर में चालू करें");

    public static string DoNotClose => T(
        "Do not close this window or turn off the computer until it finishes.",
        "No cierres esta ventana ni apagues el equipo hasta que termine.",
        "Ne fermez pas cette fenêtre et n’éteignez pas l’ordinateur avant la fin.",
        "完成前请勿关闭此窗口或关闭计算机。",
        "पूरा होने तक इस विंडो को बंद न करें और कंप्यूटर बंद न करें।");

    public static string ProtectFolderHeading(string folder) => F(T(
        "Protect “{0}” with a password", "Proteger «{0}» con contraseña", "Protéger « {0} » par mot de passe",
        "使用密码保护“{0}”", "“{0}” को पासवर्ड से सुरक्षित करें"), folder);

    public static string ProtectWarning => T(
        "All files and subfolders will be encrypted. If you forget the password, you will not be able to recover the content.",
        "Se cifrarán todos los archivos y subcarpetas. Si olvidas la contraseña, no podrás recuperar el contenido.",
        "Tous les fichiers et sous-dossiers seront chiffrés. Si vous oubliez le mot de passe, vous ne pourrez pas récupérer le contenu.",
        "所有文件和子文件夹都将被加密。如果忘记密码，将无法恢复其内容。",
        "सभी फ़ाइलें और सबफ़ोल्डर एन्क्रिप्ट किए जाएँगे। यदि आप पासवर्ड भूल गए, तो सामग्री वापस नहीं मिल सकेगी।");

    public static string PasswordLabel => T("&Password:", "&Contraseña:", "&Mot de passe :", "密码(&P)：", "पासवर्ड (&P):");

    public static string ConfirmPasswordLabel => T(
        "C&onfirm password:", "C&onfirmar contraseña:", "&Confirmer le mot de passe :", "确认密码(&O)：", "पासवर्ड की पुष्टि करें (&O):");

    public static string ShowPasswords => T(
        "&Show passwords", "&Mostrar contraseñas", "&Afficher les mots de passe", "显示密码(&S)", "पासवर्ड दिखाएँ (&S)");

    public static string PasswordsDoNotMatch => T(
        "The passwords do not match.", "Las contraseñas no coinciden.", "Les mots de passe ne correspondent pas.",
        "两次输入的密码不一致。", "पासवर्ड मेल नहीं खाते।");

    public static string FolderIsProtected(string folder) => F(T(
        "“{0}” is password-protected", "«{0}» está protegida con contraseña", "« {0} » est protégé par mot de passe",
        "“{0}”受密码保护", "“{0}” पासवर्ड से सुरक्षित है"), folder);

    public static string EnterPasswordToOpen => T(
        "Enter the password to open the folder.", "Escribe la contraseña para abrir la carpeta.",
        "Saisissez le mot de passe pour ouvrir le dossier.", "输入密码以打开文件夹。", "फ़ोल्डर खोलने के लिए पासवर्ड डालें।");

    public static string WrongPasswordTryAgain => T(
        "The password is not correct. Try again.", "La contraseña no es correcta. Inténtalo de nuevo.",
        "Le mot de passe est incorrect. Réessayez.", "密码不正确，请重试。", "पासवर्ड सही नहीं है। फिर से प्रयास करें।");

    public static string CheckingPassword => T(
        "Checking the password…", "Comprobando la contraseña…", "Vérification du mot de passe…", "正在验证密码…",
        "पासवर्ड जाँचा जा रहा है…");

    // Installing and uninstalling.

    public static string InstallTitle => T(
        "Install DoorMan", "Instalar DoorMan", "Installer DoorMan", "安装 DoorMan", "DoorMan इंस्टॉल करें");

    public static string UpdateTitle => T(
        "Update DoorMan", "Actualizar DoorMan", "Mettre à jour DoorMan", "更新 DoorMan", "DoorMan अपडेट करें");

    public static string InstallText => T(
        "DoorMan protects your folders with a password, right from File Explorer.",
        "DoorMan protege tus carpetas con contraseña desde el Explorador de archivos.",
        "DoorMan protège vos dossiers par mot de passe, directement depuis l’Explorateur de fichiers.",
        "DoorMan 让你直接在文件资源管理器中用密码保护文件夹。",
        "DoorMan आपके फ़ोल्डरों को सीधे फ़ाइल एक्सप्लोरर से पासवर्ड द्वारा सुरक्षित करता है।");

    public static string UpdateText => T(
        "The installed version will be replaced by this one. Your protected folders do not change.",
        "Se sustituirá la versión instalada por esta. Tus carpetas protegidas no cambian.",
        "La version installée sera remplacée par celle-ci. Vos dossiers protégés ne changent pas.",
        "已安装的版本将被此版本替换。受保护的文件夹不会改变。",
        "इंस्टॉल किया गया संस्करण इससे बदल दिया जाएगा। आपके सुरक्षित फ़ोल्डर नहीं बदलेंगे।");

    public static string InstallPermissionNote => T(
        "Windows will ask for permission once to add DoorMan’s options to the context menu.",
        "Windows pedirá permiso una vez para añadir las opciones de DoorMan al menú contextual.",
        "Windows demandera une seule fois l’autorisation d’ajouter les options de DoorMan au menu contextuel.",
        "Windows 会请求一次权限，以便将 DoorMan 的选项添加到上下文菜单。",
        "संदर्भ मेनू में DoorMan के विकल्प जोड़ने के लिए Windows एक बार अनुमति माँगेगा।");

    public static string Install => T("Install", "Instalar", "Installer", "安装", "इंस्टॉल करें");

    public static string Update => T("Update", "Actualizar", "Mettre à jour", "更新", "अपडेट करें");

    public static string InstallFailedTitle => T(
        "DoorMan could not be installed", "No se pudo instalar DoorMan", "Impossible d’installer DoorMan", "无法安装 DoorMan",
        "DoorMan इंस्टॉल नहीं हो सका");

    public static string InstallFailedText => T(
        "The installation did not finish. Try again.", "La instalación no ha terminado. Vuelve a intentarlo.",
        "L’installation ne s’est pas terminée. Réessayez.", "安装未完成，请重试。", "इंस्टॉलेशन पूरा नहीं हुआ। फिर से प्रयास करें।");

    public static string InstalledTitle => T(
        "DoorMan is installed", "DoorMan está instalado", "DoorMan est installé", "DoorMan 已安装", "DoorMan इंस्टॉल हो गया है");

    public static string UpdatedTitle => T(
        "DoorMan has been updated", "DoorMan se ha actualizado", "DoorMan a été mis à jour", "DoorMan 已更新",
        "DoorMan अपडेट हो गया है");

    public static string InstalledText => F(T(
        "Right-click a folder and choose {0}.\n\nTo open a protected folder, double-click it and enter the password. DoorMan starts with Windows; you can uninstall it from Settings > Apps.",
        "Haz clic derecho en una carpeta y elige {0}.\n\nPara abrir una carpeta protegida, haz doble clic en ella y escribe la contraseña. DoorMan se inicia con Windows; puedes desinstalarlo desde Configuración > Aplicaciones.",
        "Cliquez avec le bouton droit sur un dossier et choisissez {0}.\n\nPour ouvrir un dossier protégé, double-cliquez dessus et saisissez le mot de passe. DoorMan démarre avec Windows ; vous pouvez le désinstaller depuis Paramètres > Applications.",
        "右键单击文件夹并选择{0}。\n\n要打开受保护的文件夹，请双击它并输入密码。DoorMan 会随 Windows 启动；可以在“设置”>“应用”中将其卸载。",
        "किसी फ़ोल्डर पर राइट-क्लिक करें और {0} चुनें।\n\nसुरक्षित फ़ोल्डर खोलने के लिए उस पर डबल-क्लिक करें और पासवर्ड डालें। DoorMan Windows के साथ शुरू होता है; आप इसे सेटिंग्स > ऐप्स से अनइंस्टॉल कर सकते हैं।"),
        Quoted(ProtectWithPassword));

    public static string WrongArchitectureTitle => T(
        "Download the right version", "Descarga la versión adecuada", "Téléchargez la bonne version", "请下载正确的版本",
        "सही संस्करण डाउनलोड करें");

    public static string WrongArchitectureText(string file, string url) => F(T(
        "This copy of DoorMan is made for a different type of processor.\n\nDownload “{0}” from {1}",
        "Esta copia de DoorMan es para otro tipo de procesador.\n\nDescarga «{0}» desde {1}",
        "Cette copie de DoorMan est destinée à un autre type de processeur.\n\nTéléchargez « {0} » depuis {1}",
        "此 DoorMan 副本适用于其他类型的处理器。\n\n请从 {1} 下载“{0}”。",
        "DoorMan की यह प्रति किसी दूसरे प्रकार के प्रोसेसर के लिए है।\n\n{1} से “{0}” डाउनलोड करें।"), file, url);

    public static string UninstallQuestion => T(
        "Uninstall DoorMan?", "¿Desinstalar DoorMan?", "Désinstaller DoorMan ?", "要卸载 DoorMan 吗？", "DoorMan अनइंस्टॉल करें?");

    public static string UninstallText => T(
        "The context menu options and automatic startup will be removed.",
        "Se quitarán las opciones del menú contextual y el inicio automático.",
        "Les options du menu contextuel et le démarrage automatique seront supprimés.",
        "将移除上下文菜单选项和开机自动启动。",
        "संदर्भ मेनू के विकल्प और स्वचालित स्टार्टअप हटा दिए जाएँगे।");

    public static string UninstallLockedText(int locked) => F(T(
        "Locked folders: {0}. They will stay encrypted and cannot be opened until DoorMan is installed again.",
        "Carpetas bloqueadas: {0}. Seguirán cifradas y no se podrán abrir hasta volver a instalar DoorMan.",
        "Dossiers verrouillés : {0}. Ils resteront chiffrés et ne pourront pas être ouverts avant la réinstallation de DoorMan.",
        "已锁定的文件夹：{0} 个。它们将保持加密，重新安装 DoorMan 之前无法打开。",
        "लॉक किए गए फ़ोल्डर: {0}। वे एन्क्रिप्टेड रहेंगे और DoorMan दोबारा इंस्टॉल होने तक खोले नहीं जा सकेंगे।"), locked);

    public static string Uninstall => T("Uninstall", "Desinstalar", "Désinstaller", "卸载", "अनइंस्टॉल करें");

    public static string UninstalledTitle => T(
        "DoorMan has been uninstalled", "DoorMan se ha desinstalado", "DoorMan a été désinstallé", "DoorMan 已卸载",
        "DoorMan अनइंस्टॉल हो गया है");

    public static string UninstalledText => T(
        "It will no longer appear in the context menu or start with Windows.",
        "Ya no aparecerá en el menú contextual ni se iniciará con Windows.",
        "Il n’apparaîtra plus dans le menu contextuel et ne démarrera plus avec Windows.",
        "它将不再出现在上下文菜单中，也不会随 Windows 启动。",
        "यह अब संदर्भ मेनू में नहीं दिखेगा और Windows के साथ शुरू नहीं होगा।");

    public static string UsageTitle => T("DoorMan usage", "Uso de DoorMan", "Utilisation de DoorMan", "DoorMan 用法", "DoorMan का उपयोग");

    // Error notifications.

    public static string ErrorOpeningProtectedFolder => T(
        "Cannot open the protected folder", "No se puede abrir la carpeta protegida", "Impossible d’ouvrir le dossier protégé",
        "无法打开受保护的文件夹", "सुरक्षित फ़ोल्डर नहीं खुल सकता");

    public static string ErrorCompletingOperation => T(
        "Cannot complete the operation", "No se puede completar la operación", "Impossible de terminer l’opération",
        "无法完成该操作", "कार्रवाई पूरी नहीं हो सकती");

    public static string UnexpectedError => T(
        "An unexpected error occurred", "Se ha producido un error inesperado", "Une erreur inattendue s’est produite",
        "发生意外错误", "एक अनपेक्षित त्रुटि हुई");
}
