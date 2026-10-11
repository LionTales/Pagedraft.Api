using Pagedraft.Api.Models;

namespace Pagedraft.Api.Services;

/// <summary>
/// Which store holds a chapter's CURRENT prose: the chapter row or its scenes. Shared by every surface that
/// has to read "the author's manuscript" for a chapter, so they cannot disagree about it: the plain and
/// annotated exports (<see cref="BookExportService"/>) and the whole-book proofread run
/// (<see cref="Analysis.BookProofreadService"/>), which proofreads the scenes instead of the chapter row when
/// the scenes hold the writing. An export that took the scenes while the proofread ran on the frozen chapter
/// row would annotate suggestions computed on a text the file does not contain.
/// </summary>
public static class ChapterTextSource
{
    /// <summary>
    /// Whether a chapter's SCENES hold its current text, rather than the chapter row.
    ///
    /// A chapter and its scenes are two independently writable stores for one chapter's prose, and NOTHING in
    /// this product copies between them in either direction: <c>SplitScenesFromChapterAsync</c> reads
    /// <c>Chapter.ContentText</c> and writes scenes, and from that moment the chapter row is a frozen pre-split
    /// copy while the editor happily saves into either one. So an export has to decide which is the author's
    /// manuscript.
    ///
    /// THE ANSWER IS NOT "THE SCENES, WHENEVER THERE ARE SCENES", and it is worth saying why, because that is
    /// the rule this looks like it should be. The split is LOSSY: it slices plain text (so the chapter's
    /// formatting is gone), it deletes the scene-break markers themselves, it DISCARDS any segment shorter
    /// than <see cref="Analysis.SceneAutoSplitRules.MinSceneContentLength"/>, and it caps at
    /// <see cref="Analysis.SceneAutoSplitRules.MaxScenesPerChapter"/>. Right after a split the scenes are
    /// therefore a strictly poorer copy of the same prose, and preferring them would strip formatting and drop
    /// text out of the manuscript of an author who never touched a scene - data loss introduced as the cure
    /// for data loss.
    ///
    /// So the question asked is the narrower one that actually matters: HAS THE AUTHOR WRITTEN INTO THE SCENE
    /// LAYER? <c>UpdatedAt > CreatedAt</c> answers it exactly in this product. The <c>SaveChanges</c> override
    /// stamps a Scene's two timestamps equal on Add and bumps only <c>UpdatedAt</c> on Modify, the split Adds
    /// every scene in one <c>SaveChanges</c>, and the only client call that modifies a scene is the editor's
    /// content save. A scene created directly through the API with content and never touched again is the one
    /// case this reads as "not written" - it is unreachable from the client, and reading it that way keeps the
    /// pre-existing behaviour rather than inventing a new one.
    ///
    /// WHAT THIS DELIBERATELY DOES NOT DECIDE: the case where BOTH copies have been written (chapter, split,
    /// scene, then the chapter node again). Neither copy is a superset, nothing records which the author
    /// intended, and the honest resolutions all cost a persistence or product decision - see the be-c06
    /// section of the wave 3 fixes plan. This takes the scenes, and the export's
    /// <c>WarnIfBothCopiesLookWritten</c> makes it visible.
    /// </summary>
    public static bool ScenesHoldTheChaptersCurrentText(IReadOnlyList<Scene>? scenes)
        => scenes is { Count: > 0 } && scenes.Any(s => s.UpdatedAt > s.CreatedAt);
}
