namespace AIS_RubricFeedbackGenerator.Models
{
    using System.Collections.Generic;

    public class MarkingPageViewModel
    {
        // Assignment holds Tasks -> Rubrics -> Criteria -> ScoreDefinitions
        public Assignment Assignment { get; set; }

        // Which student is being marked
        public Student Student { get; set; }

        // Marking info
        public MarkingHeader? MarkingHeader { get; set; }

        // Global ScoreLevels (used for criterion + scoredef combinations)
        public List<ScoreLevel> ScoreLevels { get; set; } = new List<ScoreLevel>();
    }
}
