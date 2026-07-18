using System.Globalization;
using System.Windows;

namespace CodexWhip;

internal enum TextKey
{
    Instruction, ReadyTitle, ReadyBody, MenuWhip, MenuDiagnostics, MenuQuit,
    DiagnosticFailedTitle, StrikeSending, StrikeReceived, StrikeHeldTitle,
    SteerOne, SteerTwo, DiagnosticReadyBody, Cooldown, DraftPresent,
    NoActiveTurn, CodexNotFound, FocusFailed, InterfaceChanged
}

internal static class AppLocalizer
{
    private static readonly string[] SteeringMessages =
    [
        "Go faster. Finish the task and verify it.",
        "Move quickly. Finish the task and test it.",
        "Speed up. Complete the work and verify it.",
        "Work faster. Finish fully, then run checks.",
        "Keep moving. Complete and verify the task.",
        "Be quick. Finish all required work and test it.",
        "Proceed faster. Finish and check the result.",
        "Move faster. Complete every required step.",
        "Work quickly. Deliver and validate the result.",
        "Continue faster. Finish, check, and verify."
    ];

    private static readonly object SteeringMessageLock = new();
    private static int _lastSteeringMessageIndex = -1;

    private static readonly IReadOnlyDictionary<string, string[]> Translations = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = Split("MAKE A QUICK BACK-AND-FORTH · LEFT-CLICK TO PUT THE WHIP AWAY|Codex Whip is ready|Click the icon, then crack the whip to speed me up.|Whip Codex|Diagnostics|Quit|Codex Whip cannot strike|THE STRIKE IS ON ITS WAY…|CODEX FELT THE WHIP.|Strike blocked|Go faster. Finish the task and verify the result.|Move quickly. Complete the task and check that it works.|Codex Desktop is working and its composer is empty.|Wait a moment before the next strike.|A draft is already present in Codex and was left untouched.|Codex is open, but no active task is showing the Stop button.|The official Codex Desktop window could not be found.|Codex could not receive focus, so no text was sent.|The Codex interface changed or could not be recognized."),
        ["fr"] = Split("FAIS UN ALLER-RETOUR RAPIDE · CLIC GAUCHE POUR RANGER LE FOUET|Codex Whip est prêt|Clique sur l'icône, puis fais claquer le fouet pour me faire accélérer.|Fouetter Codex|Diagnostic|Quitter|Codex Whip ne peut pas frapper|LE COUP PART…|CODEX A REÇU LE COUP DE FOUET.|Coup retenu|Plus vite. Termine la tâche proprement, sans sacrifier la qualité.|Continue jusqu'au résultat complet, puis vérifie que tout fonctionne.|Codex Desktop travaille et son compositeur est vide.|Attends un instant avant le prochain coup.|Un brouillon est déjà présent dans Codex et a été laissé intact.|Codex est ouvert, mais aucune tâche active n'affiche le bouton Stop.|La fenêtre Codex Desktop officielle est introuvable.|Codex n'a pas obtenu le focus ; aucun texte n'a été envoyé.|L'interface Codex a changé ou n'a pas été reconnue."),
        ["es"] = Split("HAZ UN MOVIMIENTO RÁPIDO DE IDA Y VUELTA · CLIC IZQUIERDO PARA GUARDAR EL LÁTIGO|Codex Whip está listo|Haz clic en el icono y luego chasquea el látigo para acelerar el trabajo.|Azotar a Codex|Diagnóstico|Salir|Codex Whip no puede golpear|EL GOLPE ESTÁ EN CAMINO…|CODEX HA SENTIDO EL LÁTIGO.|Golpe bloqueado|Ve más rápido. Termina la tarea correctamente sin sacrificar la calidad.|Continúa hasta completar el resultado y después comprueba que funciona.|Codex Desktop está trabajando y el cuadro de texto está vacío.|Espera un momento antes del siguiente golpe.|Ya hay un borrador en Codex y se ha dejado intacto.|Codex está abierto, pero ninguna tarea activa muestra el botón Detener.|No se encontró la ventana oficial de Codex Desktop.|Codex no pudo recibir el foco, por lo que no se envió ningún texto.|La interfaz de Codex cambió o no pudo reconocerse."),
        ["de"] = Split("SCHNELLE HIN- UND HERBEWEGUNG · LINKSKLICK, UM DIE PEITSCHE WEGZULEGEN|Codex Whip ist bereit|Klicke auf das Symbol und lass die Peitsche knallen, um die Arbeit zu beschleunigen.|Codex auspeitschen|Diagnose|Beenden|Codex Whip kann nicht zuschlagen|DER SCHLAG IST UNTERWEGS…|CODEX HAT DIE PEITSCHE GESPÜRT.|Schlag blockiert|Schneller. Schließe die Aufgabe sauber ab, ohne Qualität zu opfern.|Arbeite weiter bis zum vollständigen Ergebnis und prüfe dann, ob alles funktioniert.|Codex Desktop arbeitet und das Eingabefeld ist leer.|Warte einen Moment bis zum nächsten Schlag.|In Codex ist bereits ein Entwurf vorhanden und wurde nicht verändert.|Codex ist geöffnet, aber keine aktive Aufgabe zeigt die Stopp-Schaltfläche.|Das offizielle Codex-Desktopfenster wurde nicht gefunden.|Codex konnte den Fokus nicht erhalten; es wurde kein Text gesendet.|Die Codex-Oberfläche wurde geändert oder nicht erkannt."),
        ["it"] = Split("MUOVI RAPIDAMENTE AVANTI E INDIETRO · CLIC SINISTRO PER RIPORRE LA FRUSTA|Codex Whip è pronto|Fai clic sull'icona, poi fai schioccare la frusta per accelerare il lavoro.|Frusta Codex|Diagnostica|Esci|Codex Whip non può colpire|IL COLPO STA PARTENDO…|CODEX HA SENTITO LA FRUSTA.|Colpo bloccato|Più veloce. Completa bene l'attività senza sacrificare la qualità.|Continua fino al risultato completo, poi verifica che funzioni.|Codex Desktop sta lavorando e il campo di testo è vuoto.|Attendi un momento prima del prossimo colpo.|In Codex è già presente una bozza ed è stata lasciata intatta.|Codex è aperto, ma nessuna attività attiva mostra il pulsante Interrompi.|La finestra ufficiale di Codex Desktop non è stata trovata.|Codex non ha ottenuto lo stato attivo; non è stato inviato alcun testo.|L'interfaccia di Codex è cambiata o non è stata riconosciuta."),
        ["pt"] = Split("FAÇA UM MOVIMENTO RÁPIDO DE IDA E VOLTA · CLIQUE COM O BOTÃO ESQUERDO PARA GUARDAR O CHICOTE|Codex Whip está pronto|Clique no ícone e estale o chicote para acelerar o trabalho.|Chicotear Codex|Diagnóstico|Sair|Codex Whip não pode golpear|O GOLPE ESTÁ A CAMINHO…|CODEX SENTIU O CHICOTE.|Golpe bloqueado|Mais rápido. Termine a tarefa corretamente sem sacrificar a qualidade.|Continue até o resultado estar completo e depois verifique se funciona.|O Codex Desktop está trabalhando e o campo de texto está vazio.|Aguarde um instante antes do próximo golpe.|Já existe um rascunho no Codex e ele foi mantido intacto.|O Codex está aberto, mas nenhuma tarefa ativa mostra o botão Parar.|A janela oficial do Codex Desktop não foi encontrada.|O Codex não conseguiu receber o foco; nenhum texto foi enviado.|A interface do Codex mudou ou não foi reconhecida."),
        ["nl"] = Split("MAAK EEN SNELLE HEEN-EN-WEERBEWEGING · LINKSKLIK OM DE ZWEEP OP TE BERGEN|Codex Whip is klaar|Klik op het pictogram en laat de zweep knallen om het werk te versnellen.|Codex zwepen|Diagnose|Afsluiten|Codex Whip kan niet slaan|DE SLAG KOMT ERAAN…|CODEX HEEFT DE ZWEEP GEVOELD.|Slag geblokkeerd|Sneller. Rond de taak netjes af zonder kwaliteit op te offeren.|Ga door tot het resultaat compleet is en controleer daarna of het werkt.|Codex Desktop werkt en het invoerveld is leeg.|Wacht even voor de volgende slag.|Er staat al een concept in Codex en dat is onaangeroerd gebleven.|Codex is geopend, maar geen actieve taak toont de knop Stoppen.|Het officiële Codex Desktop-venster is niet gevonden.|Codex kon geen focus krijgen; er is geen tekst verzonden.|De Codex-interface is gewijzigd of niet herkend."),
        ["pl"] = Split("WYKONAJ SZYBKI RUCH TAM I Z POWROTEM · LEWY PRZYCISK CHOWA BICZ|Codex Whip jest gotowy|Kliknij ikonę, a następnie strzel z bicza, aby przyspieszyć pracę.|Popędź Codex|Diagnostyka|Zakończ|Codex Whip nie może uderzyć|CIOS JUŻ LECI…|CODEX POCZUŁ BICZ.|Uderzenie zablokowane|Szybciej. Dokończ zadanie starannie bez obniżania jakości.|Pracuj do pełnego wyniku, a potem sprawdź, czy wszystko działa.|Codex Desktop pracuje, a pole tekstowe jest puste.|Poczekaj chwilę przed kolejnym uderzeniem.|W Codex jest już szkic i pozostawiono go bez zmian.|Codex jest otwarty, ale żadne aktywne zadanie nie pokazuje przycisku Zatrzymaj.|Nie znaleziono oficjalnego okna Codex Desktop.|Codex nie uzyskał fokusu, więc nie wysłano tekstu.|Interfejs Codex zmienił się lub nie został rozpoznany."),
        ["ru"] = Split("СДЕЛАЙТЕ БЫСТРОЕ ДВИЖЕНИЕ ТУДА-ОБРАТНО · ЛЕВЫЙ ЩЕЛЧОК УБИРАЕТ КНУТ|Codex Whip готов|Нажмите значок, затем щёлкните кнутом, чтобы ускорить работу.|Подстегнуть Codex|Диагностика|Выйти|Codex Whip не может ударить|УДАР УЖЕ ЛЕТИТ…|CODEX ПОЧУВСТВОВАЛ КНУТ.|Удар заблокирован|Быстрее. Завершите задачу аккуратно, не жертвуя качеством.|Продолжайте до полного результата, затем проверьте, что всё работает.|Codex Desktop работает, а поле ввода пусто.|Подождите немного перед следующим ударом.|В Codex уже есть черновик, и он оставлен без изменений.|Codex открыт, но ни одна активная задача не показывает кнопку остановки.|Официальное окно Codex Desktop не найдено.|Codex не получил фокус, поэтому текст не был отправлен.|Интерфейс Codex изменился или не был распознан."),
        ["uk"] = Split("ЗРОБІТЬ ШВИДКИЙ РУХ ТУДИ-Й-НАЗАД · ЛІВИЙ КЛІК ХОВАЄ БАТІГ|Codex Whip готовий|Натисніть піктограму, а потім клацніть батогом, щоб пришвидшити роботу.|Підстьобнути Codex|Діагностика|Вийти|Codex Whip не може вдарити|УДАР УЖЕ ЛЕТИТЬ…|CODEX ВІДЧУВ БАТІГ.|Удар заблоковано|Швидше. Завершіть завдання якісно, не жертвуючи якістю.|Продовжуйте до повного результату, потім перевірте, що все працює.|Codex Desktop працює, а поле введення порожнє.|Зачекайте трохи перед наступним ударом.|У Codex уже є чернетка, її залишено без змін.|Codex відкрито, але жодне активне завдання не показує кнопку зупинки.|Офіційне вікно Codex Desktop не знайдено.|Codex не отримав фокус, тому текст не було надіслано.|Інтерфейс Codex змінився або не був розпізнаний."),
        ["tr"] = Split("HIZLI BİR GİDİŞ-DÖNÜŞ HAREKETİ YAP · KIRBACI KALDIRMAK İÇİN SOL TIKLA|Codex Whip hazır|Simgeye tıkla, ardından çalışmayı hızlandırmak için kırbacı şaklat.|Codex'i kırbaçla|Tanılama|Çıkış|Codex Whip vuramıyor|DARBE GELİYOR…|CODEX KIRBACI HİSSETTİ.|Darbe engellendi|Daha hızlı. Kaliteden ödün vermeden görevi düzgünce bitir.|Sonuç tamamlanana kadar devam et, ardından çalıştığını doğrula.|Codex Desktop çalışıyor ve metin alanı boş.|Sonraki darbeden önce biraz bekle.|Codex'te zaten bir taslak var ve değiştirilmedi.|Codex açık, ancak hiçbir etkin görev Durdur düğmesini göstermiyor.|Resmî Codex Desktop penceresi bulunamadı.|Codex odak alamadı; hiçbir metin gönderilmedi.|Codex arayüzü değişti veya tanınamadı."),
        ["ar"] = Split("حرّك السوط ذهابًا وإيابًا بسرعة · انقر بزر الفأرة الأيسر لإخفائه|Codex Whip جاهز|انقر على الأيقونة، ثم اجعل السوط يفرقع لتسريع العمل.|استخدم السوط على Codex|التشخيص|خروج|يتعذر على Codex Whip تنفيذ الضربة|الضربة في طريقها…|تلقى CODEX ضربة السوط.|تم منع الضربة|تحرك بسرعة أكبر. أنهِ المهمة بإتقان من دون التضحية بالجودة.|استمر حتى تكتمل النتيجة، ثم تحقق من أنها تعمل.|يعمل Codex Desktop وحقل الكتابة فارغ.|انتظر قليلًا قبل الضربة التالية.|توجد مسودة بالفعل في Codex وقد تُركت كما هي.|Codex مفتوح، لكن لا توجد مهمة نشطة تعرض زر الإيقاف.|تعذر العثور على نافذة Codex Desktop الرسمية.|لم يتمكن Codex من الحصول على التركيز، لذلك لم يُرسل أي نص.|تغيرت واجهة Codex أو تعذر التعرف عليها."),
        ["hi"] = Split("तेज़ी से आगे-पीछे घुमाएँ · चाबुक हटाने के लिए बायाँ क्लिक करें|Codex Whip तैयार है|आइकन पर क्लिक करें, फिर काम तेज़ करने के लिए चाबुक चलाएँ।|Codex को चाबुक लगाएँ|निदान|बाहर निकलें|Codex Whip वार नहीं कर सकता|वार हो रहा है…|CODEX ने चाबुक महसूस किया।|वार रोका गया|और तेज़। गुणवत्ता से समझौता किए बिना कार्य सही ढंग से पूरा करें।|पूरा परिणाम मिलने तक जारी रखें, फिर जाँचें कि सब काम करता है।|Codex Desktop काम कर रहा है और लेखन क्षेत्र खाली है।|अगले वार से पहले थोड़ा इंतज़ार करें।|Codex में पहले से एक ड्राफ़्ट है और उसे बदला नहीं गया।|Codex खुला है, लेकिन कोई सक्रिय कार्य रोकें बटन नहीं दिखा रहा।|आधिकारिक Codex Desktop विंडो नहीं मिली।|Codex को फ़ोकस नहीं मिला, इसलिए कोई टेक्स्ट नहीं भेजा गया।|Codex इंटरफ़ेस बदल गया या पहचाना नहीं जा सका।"),
        ["id"] = Split("GERAKKAN CEPAT BOLAK-BALIK · KLIK KIRI UNTUK MENYIMPAN CAMBUK|Codex Whip siap|Klik ikon, lalu lecutkan cambuk untuk mempercepat pekerjaan.|Cambuk Codex|Diagnostik|Keluar|Codex Whip tidak dapat memukul|PUKULAN SEDANG MELUNCUR…|CODEX MERASAKAN CAMBUK.|Pukulan diblokir|Lebih cepat. Selesaikan tugas dengan rapi tanpa mengorbankan kualitas.|Lanjutkan hingga hasil lengkap, lalu pastikan semuanya berfungsi.|Codex Desktop sedang bekerja dan kolom teks kosong.|Tunggu sebentar sebelum pukulan berikutnya.|Sudah ada draf di Codex dan dibiarkan tetap utuh.|Codex terbuka, tetapi tidak ada tugas aktif yang menampilkan tombol Hentikan.|Jendela resmi Codex Desktop tidak ditemukan.|Codex tidak dapat menerima fokus, jadi tidak ada teks yang dikirim.|Antarmuka Codex berubah atau tidak dapat dikenali."),
        ["ja"] = Split("素早く往復させる · 左クリックでムチをしまう|Codex Whip の準備ができました|アイコンをクリックし、ムチを鳴らして作業を加速します。|Codex にムチを入れる|診断|終了|Codex Whip は打てません|一撃を送っています…|CODEX にムチが入りました。|一撃はブロックされました|もっと速く。品質を落とさず、タスクをきれいに完了してください。|結果が完成するまで続け、最後に正しく動くことを確認してください。|Codex Desktop は作業中で、入力欄は空です。|次の一撃まで少し待ってください。|Codex には既に下書きがあり、そのまま保持されています。|Codex は開いていますが、停止ボタンのあるアクティブなタスクがありません。|公式の Codex Desktop ウィンドウが見つかりません。|Codex にフォーカスできなかったため、テキストは送信されませんでした。|Codex の画面が変更されたか、認識できません。"),
        ["ko"] = Split("빠르게 왕복으로 움직이세요 · 왼쪽 클릭으로 채찍을 넣습니다|Codex Whip 준비 완료|아이콘을 클릭한 다음 채찍을 휘둘러 작업을 가속하세요.|Codex 채찍질|진단|종료|Codex Whip을 사용할 수 없습니다|타격을 보내는 중…|CODEX가 채찍을 느꼈습니다.|타격이 차단되었습니다|더 빠르게. 품질을 희생하지 말고 작업을 깔끔하게 완료하세요.|결과가 완성될 때까지 계속한 다음 제대로 작동하는지 확인하세요.|Codex Desktop이 작업 중이며 입력란이 비어 있습니다.|다음 타격 전까지 잠시 기다리세요.|Codex에 이미 초안이 있으며 그대로 유지했습니다.|Codex가 열려 있지만 중지 버튼이 있는 활성 작업이 없습니다.|공식 Codex Desktop 창을 찾을 수 없습니다.|Codex가 포커스를 받지 못해 텍스트를 보내지 않았습니다.|Codex 인터페이스가 변경되었거나 인식할 수 없습니다."),
        ["zh"] = Split("快速来回挥动 · 左键单击收起鞭子|Codex Whip 已就绪|单击图标，然后甩响鞭子来加快工作。|鞭策 Codex|诊断|退出|Codex Whip 无法挥击|正在挥出一击…|CODEX 感受到了鞭子。|挥击已阻止|再快一点。在不牺牲质量的前提下完整完成任务。|继续直到结果完整，然后验证一切正常。|Codex Desktop 正在工作，输入框为空。|请稍等片刻再进行下一次挥击。|Codex 中已有草稿，已保持原样。|Codex 已打开，但没有显示“停止”按钮的活动任务。|找不到官方 Codex Desktop 窗口。|Codex 未能获得焦点，因此没有发送任何文本。|Codex 界面已更改或无法识别。"),
        ["zh-Hant"] = Split("快速來回揮動 · 按滑鼠左鍵收起鞭子|Codex Whip 已就緒|按一下圖示，然後甩響鞭子來加快工作。|鞭策 Codex|診斷|結束|Codex Whip 無法揮擊|正在揮出一擊…|CODEX 感受到了鞭子。|揮擊已阻止|再快一點。在不犧牲品質的前提下完整完成任務。|繼續直到結果完整，然後確認一切正常。|Codex Desktop 正在工作，輸入框為空。|請稍等片刻再進行下一次揮擊。|Codex 中已有草稿，已保持原樣。|Codex 已開啟，但沒有顯示「停止」按鈕的活動任務。|找不到官方 Codex Desktop 視窗。|Codex 未能取得焦點，因此沒有傳送任何文字。|Codex 介面已變更或無法辨識。")
    };

    private static readonly IReadOnlyDictionary<string, string> AutoSteeringTranslations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "Auto steering",
        ["fr"] = "Steering automatique",
        ["es"] = "Steering automático",
        ["de"] = "Automatisches Steering",
        ["it"] = "Steering automatico",
        ["pt"] = "Steering automático",
        ["nl"] = "Automatische sturing",
        ["pl"] = "Automatyczne sterowanie",
        ["ru"] = "Автоматическое управление",
        ["uk"] = "Автоматичне керування",
        ["tr"] = "Otomatik yönlendirme",
        ["ar"] = "التوجيه التلقائي",
        ["hi"] = "स्वचालित निर्देशन",
        ["id"] = "Pengarahan otomatis",
        ["ja"] = "自動ステアリング",
        ["ko"] = "자동 스티어링",
        ["zh"] = "自动引导",
        ["zh-Hant"] = "自動引導"
    };

    private static readonly IReadOnlyDictionary<string, string> CliUnavailableTranslations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "Codex CLI could not be verified. Keep one active turn in classic PowerShell or Command Prompt with an empty composer.",
        ["fr"] = "Codex CLI n'a pas pu être vérifié. Garde un seul tour actif dans PowerShell classique ou l'invite de commandes, avec le compositeur vide.",
        ["es"] = "No se pudo verificar Codex CLI. Mantén un solo turno activo en PowerShell clásico o el Símbolo del sistema, con el cuadro de texto vacío.",
        ["de"] = "Codex CLI konnte nicht überprüft werden. Halte in der klassischen PowerShell oder Eingabeaufforderung genau einen aktiven Turn mit leerem Eingabefeld offen.",
        ["it"] = "Non è stato possibile verificare Codex CLI. Mantieni un solo turno attivo in PowerShell classico o nel Prompt dei comandi, con il campo di testo vuoto.",
        ["pt"] = "Não foi possível verificar o Codex CLI. Mantenha apenas um turno ativo no PowerShell clássico ou no Prompt de Comando, com o campo de texto vazio.",
        ["nl"] = "Codex CLI kon niet worden geverifieerd. Houd één actieve beurt open in klassieke PowerShell of de Opdrachtprompt, met een leeg invoerveld.",
        ["pl"] = "Nie można zweryfikować Codex CLI. Pozostaw jedną aktywną turę w klasycznym PowerShellu lub Wierszu polecenia, z pustym polem tekstowym.",
        ["ru"] = "Не удалось проверить Codex CLI. Оставьте один активный ход в классическом PowerShell или командной строке с пустым полем ввода.",
        ["uk"] = "Не вдалося перевірити Codex CLI. Залиште один активний хід у класичному PowerShell або командному рядку з порожнім полем введення.",
        ["tr"] = "Codex CLI doğrulanamadı. Klasik PowerShell veya Komut İstemi'nde, metin alanı boş olan tek bir etkin tur açık tutun.",
        ["ar"] = "تعذّر التحقق من Codex CLI. أبقِ جولة نشطة واحدة في PowerShell الكلاسيكي أو موجه الأوامر مع حقل إدخال فارغ.",
        ["hi"] = "Codex CLI को सत्यापित नहीं किया जा सका। क्लासिक PowerShell या Command Prompt में खाली इनपुट के साथ केवल एक सक्रिय टर्न खुला रखें।",
        ["id"] = "Codex CLI tidak dapat diverifikasi. Biarkan satu giliran aktif di PowerShell klasik atau Command Prompt dengan kolom input kosong.",
        ["ja"] = "Codex CLI を確認できませんでした。従来の PowerShell またはコマンド プロンプトで、入力欄が空のアクティブなターンを 1 つだけ開いてください。",
        ["ko"] = "Codex CLI를 확인할 수 없습니다. 클래식 PowerShell 또는 명령 프롬프트에서 입력란이 비어 있는 활성 턴 하나만 열어 두세요.",
        ["zh"] = "无法验证 Codex CLI。请在经典 PowerShell 或命令提示符中仅保留一个活动轮次，并确保输入框为空。",
        ["zh-Hant"] = "無法驗證 Codex CLI。請在傳統 PowerShell 或命令提示字元中只保留一個作用中的回合，並確保輸入框為空。"
    };

    internal static readonly string[] StopButtonLabels = ["Stop", "Stop generating", "Arrêter", "Annuler", "Detener", "Stopp", "Generierung stoppen", "Interrompi", "Parar", "Stoppen", "Zatrzymaj", "Остановить", "Зупинити", "Durdur", "إيقاف", "रोकें", "Hentikan", "停止", "중지", "停止生成"];
    internal static readonly string[] EmptyComposerLabels = ["Do anything", "Ask anything", "Message Codex", "Describe a task", "Demandez ce que vous voulez", "Demandez n'importe quoi", "Décrivez une tâche", "Pregunta lo que quieras", "Describe una tarea", "Frag irgendetwas", "Beschreibe eine Aufgabe", "Chiedi qualsiasi cosa", "Descrivi un'attività", "Pergunte qualquer coisa", "Descreva uma tarefa", "Vraag wat je maar wilt", "Beschrijf een taak", "Zapytaj o cokolwiek", "Opisz zadanie", "Спросите что угодно", "Опишите задачу", "Запитайте будь-що", "Опишіть завдання", "Herhangi bir şey sor", "Bir görevi açıklayın", "اسأل أي شيء", "صِف مهمة", "कुछ भी पूछें", "किसी कार्य का वर्णन करें", "Tanyakan apa saja", "Jelaskan tugas", "何でも聞いてください", "タスクを説明してください", "무엇이든 물어보세요", "작업을 설명하세요", "询问任何问题", "描述任务", "詢問任何問題", "描述任務"];

    private static readonly string[] Current = Resolve(CultureInfo.CurrentUICulture);
    internal static System.Windows.FlowDirection FlowDirection => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
        ? System.Windows.FlowDirection.RightToLeft
        : System.Windows.FlowDirection.LeftToRight;
    internal static string Text(TextKey key) => Current[(int)key];
    internal static string AutoSteeringLabel => ResolveAutoSteeringLabel(CultureInfo.CurrentUICulture);
    internal static string CliUnavailableMessage => ResolveCliUnavailableMessage(CultureInfo.CurrentUICulture);
    internal static string RandomSteeringMessage()
    {
        lock (SteeringMessageLock)
        {
            var nextIndex = _lastSteeringMessageIndex < 0
                ? Random.Shared.Next(SteeringMessages.Length)
                : Random.Shared.Next(SteeringMessages.Length - 1);
            if (_lastSteeringMessageIndex >= 0 && nextIndex >= _lastSteeringMessageIndex)
            {
                nextIndex++;
            }

            _lastSteeringMessageIndex = nextIndex;
            return SteeringMessages[nextIndex];
        }
    }

    internal static string ResultMessage(SteerResult result) => result.Code switch
    {
        "READY" => Text(TextKey.DiagnosticReadyBody),
        "COOLDOWN" => Text(TextKey.Cooldown),
        "DRAFT_PRESENT" => Text(TextKey.DraftPresent),
        "NO_ACTIVE_TURN" => Text(TextKey.NoActiveTurn),
        "CODEX_NOT_FOUND" => Text(TextKey.CodexNotFound),
        "FOCUS_FAILED" or "FOCUS_GUARD" => Text(TextKey.FocusFailed),
        "COMPOSER_STALE" or "COMPOSER_NOT_FOUND" or "UI_CHANGED" or "SEND_INPUT_FAILED" => Text(TextKey.InterfaceChanged),
        "CLI_UNAVAILABLE" or "CLI_NOT_ACTIVE" or "CLI_DRAFT_PRESENT" or "CLI_CHANGED" or "CLI_SEND_INPUT_FAILED" => CliUnavailableMessage,
        _ => result.Message
    };

    internal static bool RunSelfTest(out string message)
    {
        var expected = Enum.GetValues<TextKey>().Length;
        var invalid = Translations.FirstOrDefault(entry => entry.Value.Length != expected || entry.Value.Any(string.IsNullOrWhiteSpace));
        var english = Translations["en"];
        var universalSteers = SteeringMessages;
        if (!string.IsNullOrEmpty(invalid.Key)
            || Translations.Keys.Any(key => !AutoSteeringTranslations.ContainsKey(key))
            || Translations.Keys.Any(key => !CliUnavailableTranslations.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            || !Resolve(CultureInfo.GetCultureInfo("eo-001")).SequenceEqual(english)
            || ResolveCliUnavailableMessage(CultureInfo.GetCultureInfo("eo-001")) != CliUnavailableTranslations["en"]
            || universalSteers.Length != 10
            || universalSteers.Distinct(StringComparer.Ordinal).Count() != universalSteers.Length
            || universalSteers.Any(message => string.IsNullOrWhiteSpace(message) || message.Length > 60)
            || universalSteers.Any(message => message.Contains("whip", StringComparison.OrdinalIgnoreCase)))
        {
            message = $"FAIL: localization bundle '{invalid.Key ?? "fallback"}' is incomplete.";
            return false;
        }

        var previousSteer = RandomSteeringMessage();
        for (var index = 0; index < 100; index++)
        {
            var nextSteer = RandomSteeringMessage();
            if (nextSteer == previousSteer || !universalSteers.Contains(nextSteer, StringComparer.Ordinal))
            {
                message = "FAIL: steering messages repeated consecutively or escaped the universal English pool.";
                return false;
            }

            previousSteer = nextSteer;
        }

        message = $"PASS: {Translations.Count} UI and CLI languages are complete; unsupported cultures and steering fall back to English.";
        return true;
    }

    private static string[] Resolve(CultureInfo culture)
    {
        if (culture.Name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || culture.Name is "zh-TW" or "zh-HK" or "zh-MO")
        {
            return Translations["zh-Hant"];
        }

        return Translations.TryGetValue(culture.Name, out var exact) ? exact
            : Translations.TryGetValue(culture.TwoLetterISOLanguageName, out var language) ? language
            : Translations["en"];
    }

    private static string ResolveAutoSteeringLabel(CultureInfo culture)
    {
        var key = culture.Name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || culture.Name is "zh-TW" or "zh-HK" or "zh-MO"
            ? "zh-Hant"
            : culture.TwoLetterISOLanguageName;
        return AutoSteeringTranslations.TryGetValue(key, out var label) ? label : AutoSteeringTranslations["en"];
    }

    private static string ResolveCliUnavailableMessage(CultureInfo culture)
    {
        var key = culture.Name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || culture.Name is "zh-TW" or "zh-HK" or "zh-MO"
            ? "zh-Hant"
            : culture.TwoLetterISOLanguageName;
        return CliUnavailableTranslations.TryGetValue(key, out var message) ? message : CliUnavailableTranslations["en"];
    }

    private static string[] Split(string value) => value.Split('|');
}
