namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// The product-chat feature's config surface. One boolean today, in a feature-named section with an
/// <c>Enabled</c>-style leaf - the same shape as <c>Feedback:TriageEnabled</c>,
/// <c>Ai:AnalysisRepair:Enabled</c> and <c>LanguageEngine:LanguageTool:Enabled</c>, rather than a
/// bespoke top-level key.
/// </summary>
public class ProductChatOptions
{
    public const string SectionName = "ProductChat";

    /// <summary>
    /// WHETHER <see cref="ProductChatRouter"/>'s answer is USED (g1). False everywhere until g2 has
    /// written the per-route prompt blocks and g3 has measured them on the owner's real manuscript.
    ///
    /// <para>WHAT FALSE MEANS, PRECISELY: the route is still RESOLVED and LOGGED on every turn, because a
    /// route nobody can see is a route nobody can calibrate, and <c>ProductChatRouter</c> is pure so
    /// resolving it costs a string scan. What false suppresses is its USE: the composed prompt is forced
    /// to <see cref="ChatRoute.Union"/>, which is defined to be byte-identical to what shipped before
    /// routing existed. So a deployment that never sets this key composes exactly the prompt g4 and g5
    /// measured, and the log still says what the router would have done.</para>
    ///
    /// <para>IT IS ALSO THE ROLLBACK. g3's whole risk posture rests on the wording being revertable
    /// without a deploy, so nothing downstream may read the route from anywhere but the one place this
    /// flag gates.</para>
    ///
    /// <para>THE CLASS DEFAULT IS <c>false</c>, and the shipped <c>appsettings.json</c> states it
    /// explicitly anyway: the default is the safe posture for programmatic and test construction, and the
    /// explicit key is what makes the flag discoverable to whoever flips it in g2.</para>
    /// </summary>
    public bool RoutingEnabled { get; set; }
}
