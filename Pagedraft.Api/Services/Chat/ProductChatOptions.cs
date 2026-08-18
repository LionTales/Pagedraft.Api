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
    /// WHETHER <see cref="ProductChatRouter"/>'s answer is USED (g1). SHIPPED <c>true</c> SINCE g2, which
    /// wrote the per-route prompt blocks; g3 measures them on the owner's real manuscript, and until it
    /// passes nothing here may be described as verified.
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
    /// <para>THE CLASS DEFAULT IS STILL <c>false</c> AND g2 DID NOT CHANGE IT, only the shipped
    /// <c>appsettings.json</c> value. That divergence is deliberate: every pin test in this suite
    /// constructs the service without thinking about the flag and must keep getting the inert Union
    /// posture, which is what makes those byte-identity fences meaningful. A test that wants the ROUTED
    /// behaviour asks for it explicitly, so "which prompt did this test measure" is always visible at the
    /// call site.</para>
    /// </summary>
    public bool RoutingEnabled { get; set; }
}
