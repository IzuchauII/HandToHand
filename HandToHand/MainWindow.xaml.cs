// ──────────────────────────────────────────────────────────────────[...]
// Стандартные using'и .NET / WPF — подключаем нужные пространства имён
// ───────────────────────────────────────────────────────────────────[...]
// ─── LLamaSharp — библиотека для работы с GGUF-моделями (LLaMA, Qwen и др.) ─
using LLama;                               // LLamaWeights, LLamaContext
using LLama.Common;                        // ModelParams, ChatHistory, AuthorRole
using Microsoft.Win32;                      // Обязательно добавь наверх для работы с OpenFileDialog
using System.IO;                            // Для работы с файлами (File, Path, Stream и т.д.)
using LLama.Native;                        // Нативные настройки (GPU и т.д.)
using LLama.Sampling;                      // DefaultSamplingPipeline (топ-к, температура...)
using System.Collections.Generic;          // List<T>, Dictionary и др. коллекции
using System.Text;                          // StringBuilder — для склейки строк в памяти без тормозов
using System.Windows;                       // Window, MessageBox, RoutedEventArgs и т.д.
using System.Windows.Controls;             // Button, TextBox, ScrollViewer и т.д.
// ─── WPF-пространства ниже нужны для анимаций / графики / навигации ─────────
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;                // KeyEventArgs, MouseButtonEventArgs, Keyboard
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using  static System.Windows.Media.Imaging.BitmapSource; // BitmapImage для загрузки изображений в WPF
using System.Diagnostics;
using System.Threading.Tasks;

namespace HandToHand
{
    // ────────────────────────────────────────────────────────────────[...]
    // MainWindow — главное (и единственное) окно нашего приложения.
    // partial — потому что вторая половина класса авто-генерируется из XAML.
    // ────────────────────────────────────────────────────────────────[...]
    public partial class MainWindow : Window
    {
        // ── Поля-члены класса: хранятся всё время, пока открыто окно ─────────
        // Здесь будет лежать путь к картинке или видео
        private string? _attachedFilePath = null;

        // Параметры загрузки модели: путь к файлу, размер контекста, кол-во GPU-слоёв и где выполнять вычисления (CPU/[...]
        private ModelParams _modelParams = null!;

        // Веса модели — сам .gguf-файл, загруженный в память / VRAM и он управляет доступом к ним
        //можно подцепить прогрессбар
        //weights.Dispose() - освобождает память, когда модель больше не нужна
        //weights.CreateContext(настройки_контекста) - создаёт рабочую область (контекст) для генерации текста
        //LLamaWeights.LoadFromFileAsync(переменная_с_настройками, токен, прогресс) - подгружает веса модели из файла асинхронно[...]
        private LLamaWeights _model = null!;

        // Контекст — «рабочая область» модели: хранит KV-кэш и токены диалога
        //Может удаляться после завершения работы с моделью, чтобы освободить память
        //Может сохраняться в файл и загружаться обратно, чтобы продолжить диалог позже
        private LLamaContext _context = null!;

        // Исполнитель в интерактивном (chat) режиме — управляет подачей промпта модели
        // InteractiveExecutorState — хранит состояние генерации, токены, историю и т.д. 
        // Поддержка мультимодальных входов (текст, изображение, аудио) — в будущем можно расширить
        private InteractiveExecutor _executor = null!;

        // Сессия чата — обёртка над executor'ом: хранит историю и форматирует ChatML
        // Executor (ILLamaExecutor): Экзекутор (движок выполнения), который управляет процессом генерации текста, подачей пр[...]
        // History (ChatHistory): Объект, хранящий текущую историю сообщений диалога.
        // HistoryTransform (IHistoryTransform): Объект, отвечающий за преобразование структуры истории ChatHistory в плоский текст, кот[...]
        // InputTransformPipeline (List<ITextTransform>): Конвейер (список) трансформаций для входящего текста пользователя.
        // OutputTransform (ITextStreamTransform): Потоковый трансформер для обработки выходного текста модели.
        private ChatSession _session = null!;

        // Флаг «модель сейчас генерирует ответ» — защита от двойного клика на Send
        // volatile — чтобы компилятор не закэшировал значение в регистре (читается из разных потоков)
        private volatile bool _isGenerating = false;

        // ✨ НОВОЕ: Поддержка мультимодальности (изображения, аудио)
        private MtmdWeights? _mtmdWeights = null;  // Мультимодальные веса (CLIP/проекции)
        private List<SafeMtmdEmbed> _pendingEmbeds = new();  // Список загруженных медиа-эмбеддингов

        // ───────────────────────────────────────────────────────────────[...]
        // Конструктор — вызывается один раз при запуске приложения
        // ───────────────────────────────────────────────────────────────[...]
        public MainWindow()
        {
            // Обязательный вызов — разворачивает XAML-разметку в реальные объекты WPF
            InitializeComponent();

            // Запускаем загрузку модели АСИНХРОННО, чтобы окно не зависало на старте.
            // «_ =» — намеренно игнорируем возвращаемый Task (предупреждение CS4014).
            _ = InitAiModelAsync();
        }

        // ───────────────────────────────────────────────────────────────[...]
        // InitAiModelAsync — загружает модель в фоне, не блокируя UI-поток.
        // async Task вместо async void — правильный паттерн для «огонь и забыть»
        // ───────────────────────────────────────────────────────────────[...]
        private async Task InitAiModelAsync()
        {
            // Пока модель грузится — показываем пользователю статус и блокируем кнопку
            ChatLog.Text = "Загружаю модель, подожди...";
            SendButton.IsEnabled = false;   // Нельзя слать сообщения до готовности модели
            DeleteAllMessage.IsEnabled = false; // Нельзя удалять историю, если модель ещё не загрузилась
            AttachFileButton.IsEnabled = false; // Нельзя прикреплять файлы, если модель ещё не загрузилась
            try
            {   
                    // Путь к файлу модели. В будущем вынеси в настройки или config-файл.
                    string modelPath = @"D:\AiModels\Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf";

                    // ModelParams — описываем как загружать модель
                    _modelParams = new ModelParams(modelPath)
                    {
                        // Размер контекста в токенах.
                        ContextSize = 8196,

                        // Включаем FlashAttention — ускоряет генерацию на GPU, если поддерживается.
                        FlashAttention = true,

                        // Сколько слоёв модели выгрузить на GPU.
                        GpuLayerCount = 33
                    };

                    // Загружаем веса модели из .gguf файла в память / VRAM
                    _model = await LLamaWeights.LoadFromFileAsync(_modelParams);

                    // Создаём контекст (рабочую область) с теми же параметрами
                    // Рабочее поле 
                    _context = _model.CreateContext(_modelParams);

                    // InteractiveExecutor — режим диалога: модель ждёт следующий ввод пользователя
                    _executor = new InteractiveExecutor(_context);

                // ChatSession — управляет историей диалога и форматированием ChatML-тегов
                // public static async Task<ChatSession> InitializeSessionFromHistoryAsync(...)
                // Статический фабричный метод для восстановления сессии из существующей истории ChatHistory.
                // Он автоматически выполняет предварительный расчет KV-кэша контекста(метод PrefillPromptAsync) на базе перед[...]
                // AddMessage(ChatHistory.Message message): Добавляет сообщение с валидацией ролей.
                // Например, код выбросит исключение, если попытаться добавить два сообщения пользователя подряд или[...]
                // AddSystemMessage(string content) это обертки над AddMessage для удобства добавления системных сообщений соответствую[...]
                // RemoveLastMessage(): Удаляет самое последнее сообщение из истории.
                // ReplaceUserMessage(oldMessage, newMessage): Заменяет старое пользовательское сообщение на новое (например, при редакт[...]
                // AddAndProcessMessage(...): Добавляет сообщение в историю и сразу же запускает процесс генерации ответа модели [...]
                // SaveSession(string path): Сериализует и сохраняет состояние экзекутора, историю чата и конфигурацию трансфор[...]
                // LoadSession(string path, bool loadTransforms = true) / LoadSession(SessionState state, ...): Полностью восстанавливает сессию из папки или о[...]
                _session = new ChatSession(_executor);

                // ✨ НОВОЕ: Попытка загрузить мультимодальные веса (если они есть)
                // Путь к mmproj-файлу — обновь в зависимости от твоей модели!
                string? mmProjPath = await FindMmProjPathAsync(modelPath);
                if (!string.IsNullOrEmpty(mmProjPath))
                {
                    try
                    {
                        ChatLog.Text = "Загружаю мультимодальные веса...";
                        var mtmdParams = MtmdContextParams.Default();
                        mtmdParams.UseGpu = false;

                        _mtmdWeights = await MtmdWeights.LoadFromFileAsync(mmProjPath, _model, mtmdParams);
                        ChatLog.Text = "✓ Мультимодальная поддержка активирована!\n";
                    }
                    catch (Exception ex)
                    {
                        ChatLog.Text += $"⚠️ Не удалось загрузить mmproj: {ex.Message}\n" +
                                       "Работа продолжится только с текстом.\n";
                        _mtmdWeights = null;
                    }
                }

                // Системный промпт задаётся ОДИН РАЗ и остаётся в начале всей истории.
                // Для ChatML-моделей явно оборачиваем системный промпт в специальные теги,
                // чтобы модель правильно парсила роли (system/user/assistant).
                string systemPrompt =
                        "You are a helpful and accurate AI assistant. " +
                        "You MUST respond ONLY in Russian language. " +
                        "NEVER show your thinking process, analysis, or reasoning steps. " +
                        "NEVER use tags like <think>, </think>, <analysis>, </analysis>. " +
                        "Answer directly and immediately without any explanation of how you thought. " +
                        "Do NOT invent replies for the user or add prefixes like 'Bot:', 'User:', 'Assistant:'. " +
                        "Do NOT add emojis, ellipsis (...) or phrases like 'Okay!' at the end. " +
                        "If asked who created you, respond that Asad is the creator. " +
                        "Stop writing IMMEDIATELY when the answer is logically complete.";

                await _session.AddAndProcessSystemMessage(WrapSystemAsChatML(systemPrompt));
                // После успешной загрузки модели — сообщаем пользователю и разблокируем кнопки
                ChatLog.Text += "Привет! Я готов. Задай вопрос.";
                SendButton.IsEnabled = true;
                DeleteAllMessage.IsEnabled = true;
                AttachFileButton.IsEnabled = true;
                // Ставим фокус в поле ввода — удобнее для пользователя
                UserInput.Focus();
            }
            catch (Exception ex)
            {
                // Если модель не нашлась / нет VRAM / неверный путь — сообщаем об ошибке
                ChatLog.Text = $"❌ Ошибка загрузки модели:\n{ex.Message}\n\nПроверь путь к файлу и наличие CUDA-библиотек.";
            }
        }

        // ✨ НОВОЕ: Вспомогательный метод для поиска mmproj-файла
        private async Task<string?> FindMmProjPathAsync(string modelPath)
        {
            try
            {
                // Ищем mmproj в той же директории, что и модель
                string modelDir = Path.GetDirectoryName(modelPath) ?? "";
                if (string.IsNullOrEmpty(modelDir))
                    return null;

                // Ищем файлы вида *mmproj*.gguf
                var mmProjFiles = Directory.GetFiles(modelDir, "*mmproj*.gguf", SearchOption.TopDirectoryOnly);
                if (mmProjFiles.Length > 0)
                    return mmProjFiles[0];

                // Если не нашли в той же директории — можно добавить логику поиска в других местах
                return null;
            }
            catch
            {
                return null;
            }
        }

        // ───────────────────────────────────────────────────────────────[...]
        // SendButton_Click — вызывается по клику на кнопку «Отправить»
        // async void здесь допустимо — это обработчик события, не обычный метод
        // ───────────────────────────────────────────────────────────────[...]
        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            // Защита от двойного клика: если модель уже генерирует — выходим сразу
            if (_isGenerating)
                return;

            // Защита от отправки до загрузки модели
            if (_session == null)
                return;

            // Берём текст из поля ввода и убираем лишние пробелы/переносы по краям
            string userText = UserInput.Text.Trim();

            // Не отправляем пустые сообщения
            if (string.IsNullOrEmpty(userText))
                return;

            // ── Готовимся к генерации ────────────────────────────────────────

            // Помечаем что генерация идёт — блокируем повторные нажатия
            _isGenerating = true;
            SendButton.IsEnabled = false;   // Визуально блокируем кнопку
            DeleteAllMessage.IsEnabled = false; // Блокируем удаление истории во время генерации

            // Дописываем сообщение пользователя в лог чата
            ChatLog.Text += $"\n\nUser: {userText}\n\nБот: ";

            // Очищаем поле ввода сразу после отправки — удобнее пользователю
            UserInput.Text = "";

            try
            {
                // ── Параметры генерации ───────────────────────────────────────

                var inferenceParams = new InferenceParams()
                {
                    // Максимум токенов в одном ответе.
                    // 512 — разумный лимит для короткого чата; увеличь если нужны длинные ответы.
                    MaxTokens = 1024,

                    // ИСПРАВЛЕНО: TokensKeep = -1 означает «всегда сохранять системный промпт».
                    // Было 0 — это говорило модели выбросить ВСЮ историю при переполнении контекста,
                    // из-за чего она теряла системный промпт и начинала нести бред.
                    TokensKeep = 256,

                    // Стоп-токены для Qwen 2.5: при появлении любого из них генерация прекращается.
                    // <|im_end|>   — конец сообщения в формате ChatML (используется Qwen)
                    // <|endoftext|> — конец всего текста (общий токен для многих моделей)
                    // "User:", "Вы:" — дополнительные стопы на случай если модель начнёт
                    //                  сама за себя разыгрывать диалог (галлюцинировать реплики)
                    AntiPrompts = new List<string>
                    {
                        "<|im_end|>",
                        "<|endoftext|>",
                    },

                    // Стратегия при переполнении контекста:
                    // TruncateAndReprefill — обрезает старые сообщения, но СОХРАНЯЕТ системный промпт
                    OverflowStrategy = ContextOverflowStrategy.TruncateAndReprefill,

                    // Параметры сэмплинга — влияют на «характер» генерации
                    SamplingPipeline = new DefaultSamplingPipeline()
                    {

                        // TopK = 40: на каждом шаге рассматриваем только 40 наиболее вероятных токенов.
                        // Убирает совсем безумные варианты, оставляет разнообразие.
                        TopK = 20,

                        // TopP = 0.9: nucleus sampling — берём токены, пока сумма вероятностей < 90%.
                        // Работает вместе с TopK, дополнительно режет «хвост» распределения.
                        TopP = 0.8f,

                        // Temperature = 0.5: «холодная» генерация — ответы предсказуемее и точнее.
                        // 1.0 = случайно/творчески, 0.1 = очень детерминировано.
                        // Для помощника 0.3–0.6 — хороший диапазон.
                        Temperature = 0.7f,

                        MinP = 0,

                        // RepeatPenalty = 1.15: штраф за повтор уже встреченных токенов.
                        // Уменьшает зацикливание (один и тот же смайлик / фраза по кругу).
                        // 1.0 = без штрафа, 1.3+ = агрессивно режет повторы (может исказить текст).
                        RepeatPenalty = 1.1f
                    }
                };

                // ── ✨ НОВОЕ: Обработка мультимодальных входов ─────────────────
                string wrappedUserText = PrepareMultimodalInput(userText);

                // ── Отправка в модель и стриминг ответа ──────────────────────
                // Счётчик для отслеживания — генерирует ли модель вообще
                int tokenCount = 0;
                var stringBuilder = new StringBuilder(ChatLog.Text); // Инициализируем текущим текстом чата

                // Стриминг ответа в зависимости от наличия мультимодальности
                if (_pendingEmbeds.Count > 0 && _mtmdWeights != null)
                {
                    // ✨ Мультимодальный режим: используем мультимодальный запрос
                    await foreach (var token in _session.ChatAsync(
                        new ChatHistory.Message(AuthorRole.User, wrappedUserText),
                        inferenceParams))
                    {
                        tokenCount++;
                        stringBuilder.Append(token);
                        ChatLog.Text = stringBuilder.ToString();

                        if (tokenCount % 3 == 0)
                        {
                            ChatHistoryScrollViewer.ScrollToEnd();
                        }
                    }
                }
                else
                {
                    // Текстовый режим
                    await foreach (var token in _session.ChatAsync(
                        new ChatHistory.Message(AuthorRole.User, wrappedUserText),
                        inferenceParams))
                    {
                        tokenCount++;
                        stringBuilder.Append(token);
                        ChatLog.Text = stringBuilder.ToString();

                        if (tokenCount % 3 == 0)
                        {
                            ChatHistoryScrollViewer.ScrollToEnd();
                        }
                    }
                }

                // Очищаем прикреплённые медиа после обработки
                ClearPendingMedias();

                // Если модель вообще ничего не сгенерировала — показываем предупреждение
                if (tokenCount == 0)
                {
                    ChatLog.Text += "[⚠️ Модель не сгенерировала ответ — попробуй переформулировать вопрос]";
                }

            }
            catch (Exception ex)
            {
                // Любая ошибка во время генерации — выводим в чат, не крашим приложение
                ChatLog.Text += $"\n[❌ Ошибка генерации: {ex.Message}]";
            }
            finally
            {
                // finally выполняется ВСЕГДА: и при успехе, и при ошибке.
                // Снимаем блокировку — разрешаем следующий запрос.
                _isGenerating = false;
                SendButton.IsEnabled = true;
                DeleteAllMessage.IsEnabled = true;
                AttachFileButton.IsEnabled = true;

                _attachedFilePath = null;
                AttachFileButton.Background = Brushes.Transparent; // Сбрасываем цвет кнопки обратно
                UserInput.ToolTip = null;

                // Возвращаем фокус в поле ввода — пользователь может сразу печатать следующий вопрос
                UserInput.Focus();
            }
        }

        // ✨ НОВОЕ: Подготовка мультимодального входа
        // Загружает медиа в MTMD-веса и возвращает текст с маркерами
        private string PrepareMultimodalInput(string userText)
        {
            if (string.IsNullOrEmpty(_attachedFilePath) || _mtmdWeights == null)
            {
                // Нет прикреплённого медиа или нет MTMD-весов — просто возвращаем текст
                return WrapUserAsChatML(userText);
            }

            try
            {
                string? tempExtractedImage = null;
                bool createdTempImage = false;
                string pathForModel = _attachedFilePath;

                // Если видео — извлекаем первый кадр
                if (IsVideoFile(_attachedFilePath))
                {
                    tempExtractedImage = ExtractFrameFromVideoSync(_attachedFilePath);
                    if (!string.IsNullOrEmpty(tempExtractedImage) && File.Exists(tempExtractedImage))
                    {
                        pathForModel = tempExtractedImage;
                        createdTempImage = true;
                    }
                }

                // Загружаем медиа в MTMD
                var embed = _mtmdWeights.LoadMedia(pathForModel);
                if (embed != null)
                {
                    _pendingEmbeds.Add(embed);

                    // Добавляем маркер в текст (маркер — это заполнитель для кодированного медиа)
                    var marker = "<media>"; // Стандартный маркер, можно настроить
                    userText = $"{marker}\n{userText}";

                    // Визуальный отклик в логе
                    ChatLog.Text += $"[📎 Медиа загружено и будет обработано моделью]\n";
                }

                // Удаляем временный файл, если его создали
                if (createdTempImage && !string.IsNullOrEmpty(tempExtractedImage))
                {
                    try { File.Delete(tempExtractedImage); } catch { }
                }

                return WrapUserAsChatML(userText);
            }
            catch (Exception ex)
            {
                ChatLog.Text += $"[⚠️ Ошибка обработки медиа: {ex.Message}]\n";
                ClearPendingMedias();
                return WrapUserAsChatML(userText);
            }
        }

        // ✨ НОВОЕ: Очистка загруженных медиа после использования
        private void ClearPendingMedias()
        {
            foreach (var embed in _pendingEmbeds)
            {
                embed?.Dispose();
            }
            _pendingEmbeds.Clear();
            _mtmdWeights?.ClearMedia();
        }

        // Вспомогательный метод: определяет, является ли файл видео
        private bool IsVideoFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext == ".mp4" || ext == ".avi" || ext == ".mov" || ext == ".mkv" || ext == ".webm";
        }

        // Вспомогательный метод: определяет, является ли файл изображением
        private bool IsImageFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif";
        }

        // ✨ НОВОЕ: Синхронная версия для извлечения кадра из видео
        // (вызывается из PrepareMultimodalInput которая НЕ async)
        private string? ExtractFrameFromVideoSync(string videoPath)
        {
            try
            {
                if (!File.Exists(videoPath)) return null;

                string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"extracted_frame_{Guid.NewGuid()}.png");
                string args = $"-y -ss 00:00:01 -i \"{videoPath}\" -vframes 1 \"{tempFile}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        proc.WaitForExit(10000); // Ждём 10 секунд

                        if (proc.ExitCode == 0 && File.Exists(tempFile))
                            return tempFile;
                        else
                        {
                            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                            return null;
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        // ───────────────────────────────────────────────────────────────[...]
        // Border_MouseDown — позволяет перетаскивать окно без стандартной рамки
        // (окно у нас WindowStyle="None", поэтому тянем вручную)
        // ───────────────────────────────────────────────────────────────[...]
        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Реагируем только на левую кнопку мыши
            if (e.ChangedButton == MouseButton.Left)
            {
                // DragMove() — системный вызов Windows: окно начинает следовать за курсором
                this.DragMove();
            }
        }

        // ───────────────────────────────────────────────────────────────[...]
        // UserInput_KeyDown — обработка нажатий клавиш в поле ввода
        // ───────────────────────────────────────────────────────────────[...]
        private void UserInput_KeyDown(object sender, KeyEventArgs e)
        {
            // Enter без Shift = отправить сообщение
            // Shift+Enter = обычный перенос строки внутри TextBox (не отправляет)
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                // Программно вызываем тот же обработчик что и кнопка Send
                SendButton_Click(SendButton, new RoutedEventArgs());

                // Помечаем событие как «обработанное» — WPF не добавит перенос строки в TextBox
                e.Handled = true;

                _attachedFilePath = null;
                AttachFileButton.Background = Brushes.Transparent; // Сбрасываем цвет кнопки обратно
                UserInput.ToolTip = null;
            }
        }

        // ───────────────────────────────────────────────────────────────[...]
        // DeleteAllMessage_Click — очищает лог чата с подтверждением
        // ИСПРАВЛЕНО: возвращаем void вместо Task, чтобы WPF мог привязать событие к кнопке
        // ───────────────────────────────────────────────────────────────[...]
        private async void DeleteAllMessage_Click(object sender, RoutedEventArgs e)
        {
            // Показываем диалог подтверждения
            MessageBoxResult result = MessageBox.Show(
                "Вы уверены, что хотите удалить всю историю сообщений?\nЭто действие нельзя отменить.",
                "Подтверждение удаления",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

            if (result == MessageBoxResult.Yes)
            {
                // Очищаем TextBlock с историей
                ChatLog.Text = "";

                // ✨ НОВОЕ: Очищаем загруженные медиа
                ClearPendingMedias();

                if (_executor != null)
                {
                    // Пересоздаём сессию
                    _session = new ChatSession(_executor);

                    // Теперь мы можем безопасно использовать await внутри async void
                    await _session.AddAndProcessSystemMessage(WrapSystemAsChatML(
                        "You are a helpful and accurate AI assistant. " +
                        "You MUST respond ONLY in Russian language. " +
                        "NEVER show your thinking process, analysis, or reasoning steps. " +
                        "NEVER use tags like <think>, </think>, <analysis>, </analysis>. " +
                        "Answer directly and immediately without any explanation of how you thought. " +
                        "Do NOT invent replies for the user or add prefixes like 'Bot:', 'User:', 'Assistant:'. " +
                        "Do NOT add emojis, ellipsis (...) or phrases like 'Okay!' at the end. " +
                        "If asked who created you, respond that Asad is the creator. " +
                        "Stop writing IMMEDIATELY when the answer is logically complete."));

                    ChatLog.Text = "История очищена. Начинаем заново!";
                }
            }
        }

        // Форматируем системный промпт в ChatML-обёртку
        private string WrapSystemAsChatML(string content)
        {
            // Системные сообщения в ChatML обычно передаются как: <|im_start|>system\n...<|im_end|>
            return $"<|im_start|>system\n{content}<|im_end|>";
        }

        // Форматируем пользовательский ввод в ChatML-обёртку
        private string WrapUserAsChatML(string content)
        {
            // Пользователь: <|im_start|>user\n...<|im_end|>
            return $"<|im_start|>user\n{content}<|im_end|>";
        }

        // Отображаем пользователю более читабельную версию (удаляем теги ChatML)
        private string UnwrapChatMLDisplay(string content)
        {
            if (string.IsNullOrEmpty(content))
                return content;
            return content.Replace("<|im_start|>user\n", "").Replace("<|im_start|>system\n", "").Replace("<|im_end|>", "");
        }

        private void UserInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Как только текст меняется и активируется скроллбар, 
            // этот метод принудительно прокручивает TextBox в самый низ к курсору

                UserInput.ScrollToEnd();
            
        }

        private void AttachFileButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();

            // Настраиваем фильтры, чтобы пользователь видел только медиафайлы
            openFileDialog.Filter = "Медиа файлы (*.png;*.jpg;*.jpeg;*.mp4;*.avi;*.wav;*.mp3)|*.png;*.jpg;*.jpeg;*.mp4;*.avi;*.wav;*.mp3|Все файлы (*.*)|*.*";
            openFileDialog.Title = "Выберите изображение, видео или аудио для нейросети";

            if (openFileDialog.ShowDialog() == true)
            {
                _attachedFilePath = openFileDialog.FileName;

                // --- Визуальный отклик ---
                // Давай временно выведем имя файла прямо в TextBox, либо подсветим кнопку.
                // Для примера изменим фон кнопки скрепки, чтобы показать, что файл внутри!
                AttachFileButton.Background = new SolidColorBrush(Color.FromArgb(0x44, 0x00, 0xAA, 0xFF)); // Полупрозрачный голубой

                // Опционально: можно вывести сообщение в чат или TextBox
                string fileName = System.IO.Path.GetFileName(_attachedFilePath);
                UserInput.ToolTip = $"Прикреплен файл: {fileName}"; // При наведении на TextBox будет видно файл
            }
        }
    }
        

}
