using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Ask;

namespace TarkovCompanion.App.ViewModels.V2.Ask;

/// <summary>One link under an answer: its words and what it opens.</summary>
public sealed record AskLinkViewModel(string Label, string AutomationId, ICommand OpenCommand);

/// <summary>A closest name the player can ask about instead, one tap.</summary>
public sealed record AskSuggestionViewModel(string Name, string Label, ICommand AskCommand);

/// <summary>
/// [#712 2-5] The Ask mode of the Ctrl+K palette: a question typed there is answered from the app's
/// own data, on a card above the command list.
/// </summary>
/// <remarks>
/// <para>
/// A question is anything <see cref="AskGrammar.LooksLikeQuestion"/> accepts; everything else is a
/// command search and the palette behaves exactly as before. Typing is debounced so an answer is
/// looked up once the words stop changing, and a newer question cancels an older lookup, so a slow
/// answer can never replace the card for what is typed now.
/// </para>
/// <para>
/// Links hand an <see cref="AskLink"/> back to the shell, which owns navigation.
/// </para>
/// </remarks>
public sealed class AskViewModel : BindableViewModel, IDisposable
{
    /// <summary>How long the words must stay still before they are looked up.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly AskService _service;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _debounce;
    private CancellationTokenSource? _pendingCts;
    private Action<AskLink>? _open;
    private AskAnswer? _answer;
    private string _query = string.Empty;
    private bool _isAsking;

    public AskViewModel(AskService service, TimeProvider? clock = null, TimeSpan? debounce = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _clock = clock ?? TimeProvider.System;
        _debounce = debounce ?? Debounce;
    }

    /// <summary>The lookup in flight, if any; a render or a test waits on it.</summary>
    public Task? PendingAnswer { get; private set; }

    /// <summary>Whether the palette shows the Ask card: the words are a question.</summary>
    public bool IsShowing => AskGrammar.LooksLikeQuestion(_query);

    public bool IsAsking
    {
        get => _isAsking;
        private set => SetProperty(ref _isAsking, value);
    }

    public AskAnswer? Answer
    {
        get => _answer;
        private set
        {
            if (SetProperty(ref _answer, value))
            {
                RaiseCard();
            }
        }
    }

    public string HeadingLabel => AskText.Heading;

    public string AskingLabel => AskText.Asking;

    public string HintLabel => AskText.Hint;

    /// <summary>With nothing typed, the palette says it answers questions too.</summary>
    public bool ShowsHint => string.IsNullOrWhiteSpace(_query);

    public bool HasAnswer => Answer is not null && !IsAsking;

    public string IntentLabel => AskText.Intent(Answer?.Intent);

    /// <summary>The thing answered about, as the catalog names it; empty for an unanswered question.</summary>
    public string Title => Answer is { IsAnswered: true } answer ? answer.Heading : string.Empty;

    public bool HasTitle => Title.Length > 0;

    /// <summary>Why there is no answer, in words; empty when there is one.</summary>
    public string Unanswered => Answer is { Unanswered: { } reason } answer ? AskText.Unanswered(reason, answer.Heading) : string.Empty;

    public bool IsUnanswered => Answer is { IsAnswered: false };

    public IReadOnlyList<string> Lines => Answer is { } answer ? [.. answer.Lines.Select(AskText.Line)] : [];

    /// <summary>Every source, with its age, on one line: "From the catalog, updated … · Modelled".</summary>
    public string SourceLine
    {
        get
        {
            if (Answer is not { } answer)
            {
                return string.Empty;
            }

            var now = _clock.GetUtcNow();
            return string.Join(" · ", answer.Sources.Select(source => AskText.Source(source, now)).Where(text => text.Length > 0).Distinct());
        }
    }

    public bool HasSourceLine => SourceLine.Length > 0;

    /// <summary>Near-equal names beside an answer ("Also: …"), or the closest names instead of one.</summary>
    public string ClosestLine => Answer is { Closest.Count: > 0 } answer
        ? answer.IsAnswered ? AskText.Also(string.Join(", ", answer.Closest)) : AskText.Closest(string.Join(", ", answer.Closest))
        : string.Empty;

    public bool HasClosestLine => ClosestLine.Length > 0;

    public IReadOnlyList<AskSuggestionViewModel> Suggestions => Answer is { IsAnswered: false, Closest.Count: > 0 } answer
        ? [.. answer.Closest.Take(4).Select(name => new AskSuggestionViewModel(name, AskText.TryClosest(name), new DelegateCommand(() => AskAbout(answer, name))))]
        : [];

    public bool HasSuggestions => Suggestions.Count > 0;

    public IReadOnlyList<AskLinkViewModel> Links => Answer is { } answer
        ? [.. answer.Links.Select((link, index) => new AskLinkViewModel(
            AskText.Link(link),
            $"v2-ask-link-{link.Kind.ToString().ToLowerInvariant()}-{index}",
            new DelegateCommand(() => _open?.Invoke(link))))]
        : [];

    public bool HasLinks => Links.Count > 0;

    public string Query => _query;

    /// <summary>What a link does: set by the shell, which owns navigation.</summary>
    public void AttachNavigation(Action<AskLink> open) => _open = open ?? throw new ArgumentNullException(nameof(open));

    /// <summary>What the palette's box holds now; a question is looked up once typing pauses.</summary>
    public void SetQuery(string? text)
    {
        _query = text ?? string.Empty;
        _pendingCts?.Cancel();
        _pendingCts?.Dispose();
        _pendingCts = null;
        OnPropertyChanged(nameof(IsShowing));
        OnPropertyChanged(nameof(ShowsHint));
        OnPropertyChanged(nameof(Query));
        if (!IsShowing)
        {
            IsAsking = false;
            Answer = null;
            PendingAnswer = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _pendingCts = cts;
        IsAsking = true;
        RaiseCard();
        PendingAnswer = LookUpAsync(_query, cts.Token);
    }

    public void Dispose()
    {
        _pendingCts?.Cancel();
        _pendingCts?.Dispose();
        _pendingCts = null;
    }

    private async Task LookUpAsync(string question, CancellationToken cancellationToken)
    {
        try
        {
            if (_debounce > TimeSpan.Zero)
            {
                await Task.Delay(_debounce, _clock, cancellationToken).ConfigureAwait(true);
            }

            var answer = await _service.AskAsync(question.Trim().Trim('?').Trim(), cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Answer = answer;
            IsAsking = false;
            RaiseCard();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer question replaced this one.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed read is not an answer: say it could not be answered rather than guess.
            Answer = AskAnswer.Cannot(AskUnanswered.NoData);
            IsAsking = false;
            RaiseCard();
        }
    }

    /// <summary>Asks the same question about a closest name instead of the words that matched nothing.</summary>
    private void AskAbout(AskAnswer answer, string name)
    {
        var subject = answer.Heading;
        var text = subject.Length > 0 && _query.Contains(subject, StringComparison.OrdinalIgnoreCase)
            ? ReplaceIgnoringCase(_query, subject, name)
            : answer.Intent switch
            {
                AskIntent.ItemUses => $"where is {name} used",
                AskIntent.ItemSources => $"where can I get {name}",
                AskIntent.Ammo => $"best {name}",
                _ => $"what do I need for {name}",
            };
        QueryReplaced?.Invoke(this, text);
    }

    /// <summary>Raised when a suggestion rewrites the question; the palette puts it in its box.</summary>
    public event EventHandler<string>? QueryReplaced;

    private static string ReplaceIgnoringCase(string text, string find, string replacement)
    {
        var index = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + find.Length));
    }

    private void RaiseCard()
    {
        OnPropertyChanged(nameof(HasAnswer));
        OnPropertyChanged(nameof(IntentLabel));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasTitle));
        OnPropertyChanged(nameof(Unanswered));
        OnPropertyChanged(nameof(IsUnanswered));
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(SourceLine));
        OnPropertyChanged(nameof(HasSourceLine));
        OnPropertyChanged(nameof(ClosestLine));
        OnPropertyChanged(nameof(HasClosestLine));
        OnPropertyChanged(nameof(Suggestions));
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(Links));
        OnPropertyChanged(nameof(HasLinks));
    }
}
