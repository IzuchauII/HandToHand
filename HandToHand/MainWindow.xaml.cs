// ─────────────────────────────────────────────────────────────────────────────
// Стандартные using'и .NET / WPF — подключаем нужные пространства имён
// ─────────────────────────────────────────────────────────────────────────────
using System.Text;                          // StringBuilder — для склейки строк в памяти без тормозов
using System.Collections.Generic;          // List<T>, Dictionary и др. коллекции
using System.Windows;                       // Window, MessageBox, RoutedEventArgs и т.д.
using System.Windows.Controls;             // Button, TextBox, ScrollViewer и т.д.
using System.Windows.Input;                // KeyEventArgs, MouseButtonEventArgs, Keyboard
// ─── WPF-пространства ниже нужны для анимаций / графики / навигации ─────────
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
// ─── LLamaSharp — библиотека для работы с GGUF-моделями (LLaMA, Qwen и др.) ─
using LLama;                               // LLamaWeights, LLamaContext
using LLama.Common;                        // ModelParams, ChatHistory, AuthorRole
using LLama.Native;                        // Нативные настройки (GPU и т.д.)
using LLama.Sampling;                      // DefaultSamplingPipeline (топ-к, температура...)

namespace HandToHand
{
    // ─────────────────────────────────────────────────────────────────────────
    // MainWindow — главное (и единственное) окно нашего приложения.
    // partial — потому что вторая половина класса авто-генерируется из XAML.
    // ─────────────────────────────────────────────────────────────────────────
    public partial class MainWindow : Window
    {
        // ── Поля-члены класса: хранятся всё время, пока открыто окно ─────────

        // Параметры загрузки модели: путь к файлу, размер контекста, кол-во GPU-слоёв и где выполнять вычисления (CPU/GPU)
        private ModelParams _modelParams = null!;

        // Веса модели — сам .gguf-файл, загруженный в память / VRAM и он управляет доступом к ним
        //можно подцепить прогрессбар
        //weights.Dispose() - освобождает память, когда модель больше не нужна
        //weights.CreateContext(настройки_контекста) - создаёт рабочую область (контекст) для генерации текста
        //LLamaWeights.LoadFromFileAsync(переменная_с_настройками, токен, прогресс) - подгружает веса модели из файла асинхронно, с возможностью отмены и отслеживания прогресса
        private LLamaWeights _model = null!;

        // Контекст — «рабочая область» модели: хранит KV-кэш и токены диалога
        private LLamaContext _context = null!;

        // Исполнитель в интерактивном (chat) режиме — управляет подачей промпта модели
        private InteractiveExecutor _executor = null!;

        // Сессия чата — обёртка над executor'ом: хранит историю и форматирует ChatML
        private ChatSession _session = null!;

        // Флаг «модель сейчас генерирует ответ» — защита от двойного клика на Send
        // volatile — чтобы компилятор не закэшировал значение в регистре (читается из разных потоков)
        private volatile bool _isGenerating = false;

        // ─────────────────────────────────────────────────────────────────────
        // Конструктор — вызывается один раз при запуске приложения
        // ─────────────────────────────────────────────────────────────────────
        public MainWindow()
        {
            // Обязательный вызов — разворачивает XAML-разметку в реальные объекты WPF
            InitializeComponent();

            // Запускаем загрузку модели АСИНХРОННО, чтобы окно не зависало на старте.
            // «_ =» — намеренно игнорируем возвращаемый Task (предупреждение CS4014).
            _ = InitAiModelAsync();
        }

        // ─────────────────────────────────────────────────────────────────────
        // InitAiModelAsync — загружает модель в фоне, не блокируя UI-поток.
        // async Task вместо async void — правильный паттерн для «огонь и забыть»
        // ─────────────────────────────────────────────────────────────────────
        private async Task InitAiModelAsync()
        {
            // Пока модель грузится — показываем пользователю статус и блокируем кнопку
            ChatLog.Text = "Загружаю модель, подожди...";
            SendButton.IsEnabled = false;   // Нельзя слать сообщения до готовности модели
            DeleteAllMessage.IsEnabled = false; // Нельзя удалять историю, если модель ещё не загрузилась
            try
            {
                // Task.Run переносит тяжёлую синхронную работу в пул потоков,
                // чтобы UI (главный поток) не завис на время загрузки весов модели
                await Task.Run(() =>
                {
                    // Путь к файлу модели. В будущем вынеси в настройки или config-файл.
                    string modelPath = @"D:\AiModels\Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf";

                    // ModelParams — описываем как загружать модель
                    _modelParams = new ModelParams(modelPath)
                    {
                        // Размер контекста в токенах. 4096 — стандарт для Qwen 1.5B.
                        // Если не хватает VRAM — снизь до 2048.
                        ContextSize = 8196,

                        FlashAttention = true,

                        // Сколько слоёв модели выгрузить на GPU.
                        // Qwen 1.5B Q8 имеет 28 слоёв — все на GPU.
                        // Если VRAM мало — уменьшай (например, 14 = половина на GPU, остальное CPU).
                        GpuLayerCount = 33
                    };
                    //Исправить надо на асинхронную загрузку, чтобы не блокировать UI-поток. LLamaSharp поддерживает асинхронную загрузку весов модели через метод LoadFromFileAsync, который принимает ModelParams, CancellationToken и IProgress<float> для отслеживания прогресса загрузки. Это позволит пользователю видеть прогресс загрузки модели и при необходимости отменить её.
                    // Загружаем веса модели из .gguf файла в память / VRAM
                    _model = LLamaWeights.LoadFromFile(_modelParams);

                    // Создаём контекст (рабочую область) с теми же параметрами
                    _context = _model.CreateContext(_modelParams);

                    // InteractiveExecutor — режим диалога: модель ждёт следующий ввод пользователя
                    _executor = new InteractiveExecutor(_context);

                    // ChatSession — управляет историей диалога и форматированием ChatML-тегов
                    _session = new ChatSession(_executor);

                    // Системный промпт задаётся ОДИН РАЗ и остаётся в начале всей истории.
                    // Он говорит модели КАК себя вести во всём чате.
                    _session.History.AddMessage(
                        AuthorRole.System,
                        "You are a helpful, concise and accurate AI assistant. " +
                        "You MUST respond ONLY in Russian language. " +
                        "NEVER show your thinking process, analysis, or reasoning steps. " +
                        "NEVER use tags like <think>, </think>, <analysis>, </analysis>. " +
                        "Answer directly and immediately without any explanation of how you thought. " +
                        "Do NOT invent replies for the user or add prefixes like 'Bot:', 'User:', 'Assistant:'. " +
                        "Do NOT add emojis, ellipsis (...) or phrases like 'Okay!' at the end. " +
                        "If asked who created you, respond that Asad is the creator. " +
                        "Stop writing IMMEDIATELY when the answer is logically complete."
                    );
                });

                // Возвращаемся в UI-поток (после await Task.Run мы автоматически здесь)
                // и разблокируем интерфейс
                ChatLog.Text = "Привет! Я готов. Задай вопрос.";
                SendButton.IsEnabled = true;
                DeleteAllMessage.IsEnabled = true;
                // Ставим фокус в поле ввода — удобнее для пользователя
                UserInput.Focus();
            }
            catch (Exception ex)
            {
                // Если модель не нашлась / нет VRAM / неверный путь — сообщаем об ошибке
                ChatLog.Text = $"❌ Ошибка загрузки модели:\n{ex.Message}\n\nПроверь путь к файлу и наличие CUDA-библиотек.";
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // SendButton_Click — вызывается по клику на кнопку «Отправить»
        // async void здесь допустимо — это обработчик события, не обычный метод
        // ─────────────────────────────────────────────────────────────────────
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

            // Дописываем сообщение пользователя в лог чата
            ChatLog.Text += $"\n\nВы: {userText}\nБот: ";

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
                        "<|im_end|>",       // ChatML конец сообщения
                        "<|endoftext|>",    // Конец документа
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

                // ── Отправка в модель и стриминг ответа ──────────────────────
                // Счётчик для отслеживания — генерирует ли модель вообще
                int tokenCount = 0;
                // ИСПРАВЛЕНО: ChatAsync принимает ChatHistory.Message; создаём сообщение в истории.
                // Ранее был вызов с string, что приводило к CS1503.
                await foreach (var token in _session.ChatAsync(
                    new ChatHistory.Message(AuthorRole.User, userText),
                    inferenceParams))
                {
                    tokenCount++;
                    ChatLog.Text += token;
                    ChatHistoryScrollViewer.ScrollToEnd();
                }

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

                // Возвращаем фокус в поле ввода — пользователь может сразу печатать следующий вопрос
                UserInput.Focus();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Border_MouseDown — позволяет перетаскивать окно без стандартной рамки
        // (окно у нас WindowStyle="None", поэтому тянем вручную)
        // ─────────────────────────────────────────────────────────────────────
        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Реагируем только на левую кнопку мыши
            if (e.ChangedButton == MouseButton.Left)
            {
                // DragMove() — системный вызов Windows: окно начинает следовать за курсором
                this.DragMove();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // UserInput_KeyDown — обработка нажатий клавиш в поле ввода
        // ─────────────────────────────────────────────────────────────────────
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
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // DeleteAllMessage_Click — очищает лог чата с подтверждением
        // ─────────────────────────────────────────────────────────────────────
        private void DeleteAllMessage_Click(object sender, RoutedEventArgs e)
        {
            // Показываем диалог подтверждения — пользователь должен осознанно удалить историю
            MessageBoxResult result = MessageBox.Show(
                "Вы уверены, что хотите удалить всю историю сообщений?\nЭто действие нельзя отменить.",
                "Подтверждение удаления",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning  // ИСПРАВЛЕНО: Hand — не самая подходящая иконка; Warning нагляднее
            );

            if (result == MessageBoxResult.Yes)
            {
                // Очищаем TextBlock с историей
                ChatLog.Text = "";

                // ВАЖНО: очищаем историю в самой сессии модели!
                // Иначе модель «помнит» старый диалог даже после очистки экрана.
                // Пересоздаём сессию — самый надёжный способ сбросить состояние.
                if (_executor != null)
                {
                    _session = new ChatSession(_executor);

                    // Заново добавляем системный промпт в свежую сессию
                    _session.History.AddMessage(
                        AuthorRole.System,
                        "You are a helpful, concise and accurate AI assistant. " +
                        "You MUST respond ONLY in Russian language. " +
                        "NEVER show your thinking process, analysis, or reasoning steps. " +
                        "NEVER use tags like <think>, </think>, <analysis>, </analysis>. " +
                        "Answer directly and immediately without any explanation of how you thought. " +
                        "Do NOT invent replies for the user or add prefixes like 'Bot:', 'User:', 'Assistant:'. " +
                        "Do NOT add emojis, ellipsis (...) or phrases like 'Okay!' at the end. " +
                        "If asked who created you, respond that Asad is the creator. " +
                        "Stop writing IMMEDIATELY when the answer is logically complete."
                    );
                }

                ChatLog.Text = "История очищена. Начинаем заново!";
            }
        }
    }
        

}