using AIS_RubricFeedbackGenerator.Data;
using AIS_RubricFeedbackGenerator.Models;
using AIS_RubricFeedbackGenerator.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AIS_RubricFeedbackGenerator.Controllers
{
    [Route("Marking")]
    public class MarkingController : Controller
    {
        private readonly AIS_RubricFeedbackGeneratorContext _context;
        private readonly QuestPDFService _pdfService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<MarkingController> _logger;

        public MarkingController(AIS_RubricFeedbackGeneratorContext context, QuestPDFService pdfService, IHttpClientFactory httpClientFactory, ILogger<MarkingController> logger)
        {
            _context = context;
            _pdfService = pdfService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        // Step 1: List all assignments
        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            var assignments = await _context.Assignments
                .Include(a => a.Course)
                .ToListAsync();

            return View(assignments);
        }

        // Step 2: List students for selected assignment
        [HttpGet("Students/{assignmentId}")]
        public async Task<IActionResult> Students(string assignmentId)
        {
            var assignment = await _context.Assignments
                .Include(a => a.Course)
                .FirstOrDefaultAsync(a => a.AssignmentId == assignmentId);

            if (assignment == null)
                return NotFound("Assignment not found.");

            ViewBag.AssignmentId = assignment.AssignmentId; // pass to Razor link

            var studentStatuses = await _context.StudentCourses
                .Include(sc => sc.Student)
                .Where(sc => sc.CourseId == assignment.CourseId)
                .Select(sc => new StudentMarkingViewModel
                {
                    StudentId = sc.StudentId,
                    FullName = sc.Student.FullName,
                    Email = sc.Student.Email,
                    MarkingStatus = _context.MarkingHeaders
                .Where(mh => mh.AssignmentId == assignmentId && mh.StudentId == sc.StudentId)
                .Select(mh => mh.MarkingStatus)   // either "draft", "submitted", or null
                .FirstOrDefault()
                })
                .ToListAsync();

            return View(studentStatuses);
        }
        // Step 3: Mark selected student for the assignment
        [HttpGet("Students/{assignmentId}/MarkStudent/{studentId}")]
        public async Task<IActionResult> Mark(string assignmentId, string studentId)
        {
            if (string.IsNullOrEmpty(studentId))
                return BadRequest("Student ID is required.");

            var selectedAssignment = await _context.Assignments
                .Include(a => a.Course)
                .Include(a => a.Tasks)
                    .ThenInclude(t => t.Rubrics)
                        .ThenInclude(r => r.Criteria)
                .Include(a => a.Tasks)
                    .ThenInclude(t => t.Rubrics)
                        .ThenInclude(r => r.ScoreDefinitions)
                .FirstOrDefaultAsync(a => a.AssignmentId == assignmentId);

            if (selectedAssignment == null)
                return NotFound("Assignment not found.");

            var student = await _context.Students
                .FirstOrDefaultAsync(s => s.StudentId == studentId);

            if (student == null)
                return NotFound("Student not found.");

            var markingHeader = await _context.MarkingHeaders
                .Include(m => m.MarkingDetails)
                .FirstOrDefaultAsync(m => m.AssignmentId == assignmentId && m.StudentId == studentId);

            var scoreLevels = await _context.ScoreLevels.ToListAsync();

            var viewModel = new MarkingPageViewModel
            {
                Assignment = selectedAssignment,
                Student = student,
                MarkingHeader = markingHeader,
                ScoreLevels = scoreLevels
            };

            return View("Mark", viewModel);
        }

        [HttpPost]
        public async Task<IActionResult> SaveMarking(string AssignmentId, string StudentId, Dictionary<string, string> SelectedScores, string action)
        {
            if (string.IsNullOrEmpty(AssignmentId) || string.IsNullOrEmpty(StudentId))
                return BadRequest("AssignmentId and StudentId are required.");

            // Load or create marking header
            var markingHeader = await _context.MarkingHeaders
                .Include(m => m.MarkingDetails)
                .FirstOrDefaultAsync(m => m.AssignmentId == AssignmentId && m.StudentId == StudentId);
            string yearPrefix = DateTime.Now.ToString("yy");

            // Generate MH ID
            var lastMHId = await _context.MarkingHeaders
                    .Where(r => r.MarkingHeaderId.StartsWith($"MH{yearPrefix}"))
                    .OrderByDescending(r => r.MarkingHeaderId)
                    .FirstOrDefaultAsync();
            int lastMHNum = lastMHId != null ? int.Parse(lastMHId.MarkingHeaderId.Substring(4)) : 0;

            string newMHid = $"MH{yearPrefix}{(lastMHNum + 1):D4}";

            if (markingHeader == null)
            {

                markingHeader = new MarkingHeader
                {
                    MarkingHeaderId = newMHid,
                    AssignmentId = AssignmentId,
                    StudentId = StudentId,
                    MarkingDetails = new List<MarkingDetail>(),
                    MarkingStatus = action == "submit" ? "submitted" : "draft"
                };
                _context.MarkingHeaders.Add(markingHeader);
            }
            else
            {
                // Ensure collection is not null
                markingHeader.MarkingStatus = action == "submit" ? "submitted" : "draft";
                markingHeader.MarkingDetails ??= new List<MarkingDetail>();
            }

            // Load related data for FK references
            var criteriaList = await _context.Criteria
                .Include(c => c.Rubric)
                .ThenInclude(r => r.Task)
                .Where(c => SelectedScores.Keys.Contains(c.CriterionId))
                .ToListAsync();

            // Update marking details
            foreach (var entry in SelectedScores)
            {
                var criterionId = entry.Key;
                var scoreDefId = entry.Value;

                var criterion = criteriaList.FirstOrDefault(c => c.CriterionId == criterionId);
                if (criterion == null) continue;

                var rubricId = criterion.RubricId;
                var taskId = criterion.Rubric?.TaskId;

                var detail = markingHeader.MarkingDetails
                    .FirstOrDefault(md => md.CriterionId == criterionId);

                var lastMDId = await _context.MarkingDetails
                    .Where(r => r.MarkingDetailId.StartsWith($"MD{yearPrefix}"))
                    .OrderByDescending(r => r.MarkingDetailId)
                    .FirstOrDefaultAsync();
                int lastMDNum = lastMDId != null ? int.Parse(lastMDId.MarkingDetailId.Substring(4)) : 0;

                var scoreLevel = await _context.ScoreLevels
                    .FirstOrDefaultAsync(sl => sl.CriterionId == criterionId && sl.ScoreDefinitionId == scoreDefId);

                if (scoreLevel == null)
                    continue; // or handle error


                string newMDid = $"MD{yearPrefix}{(lastMHNum + 1):D4}";

                if (detail == null)
                {
                    // Create new MarkingDetail as before
                    detail = new MarkingDetail
                    {
                        MarkingDetailId = newMDid,
                        MarkingHeaderId = markingHeader.MarkingHeaderId,
                        TaskId = taskId,
                        RubricId = rubricId,
                        CriterionId = criterionId,
                        ScoreDefinitionId = scoreDefId,
                        ScoreLevelId = scoreLevel.ScoreLevelId // or set properly if needed
                    };
                    markingHeader.MarkingDetails.Add(detail);
                }
                else
                {
                    // Instead of modifying the key → delete & recreate
                    _context.MarkingDetails.Remove(detail);
                    await _context.SaveChangesAsync(); // Must save before inserting new one!

                    // Generate fresh ID again (or reuse `newMDid`)
                    var lastMDId2 = await _context.MarkingDetails
                        .Where(r => r.MarkingDetailId.StartsWith($"MD{yearPrefix}"))
                        .OrderByDescending(r => r.MarkingDetailId)
                        .FirstOrDefaultAsync();
                    int lastMDNum2 = lastMDId2 != null ? int.Parse(lastMDId2.MarkingDetailId.Substring(4)) : 0;
                    string newMDid2 = $"MD{yearPrefix}{(lastMHNum + 1):D4}";

                    var newDetail = new MarkingDetail
                    {
                        MarkingDetailId = newMDid2,
                        MarkingHeaderId = markingHeader.MarkingHeaderId,
                        TaskId = taskId,
                        RubricId = rubricId,
                        CriterionId = criterionId,
                        ScoreDefinitionId = scoreDefId,
                        ScoreLevelId = scoreLevel.ScoreLevelId
                    };
                    markingHeader.MarkingDetails.Add(newDetail);
                }

            }

            // Save changes
            await _context.SaveChangesAsync();

            if (action == "submit")
            {
                TempData["Success"] = "Student successfully marked.";
                return RedirectToAction("Students", new { assignmentId = AssignmentId });
            }

            TempData["Success"] = "Marking progress saved.";
            return RedirectToAction("Mark", new { assignmentId = AssignmentId, studentId = StudentId });
        }

        [HttpPost("ResetMarking")]
        public async Task<IActionResult> ResetMarking(string assignmentId, string studentId)
        {
            if (string.IsNullOrEmpty(assignmentId) || string.IsNullOrEmpty(studentId))
                return BadRequest("AssignmentId and StudentId are required.");

            var markingHeader = await _context.MarkingHeaders
                .Include(m => m.MarkingDetails)
                .FirstOrDefaultAsync(m => m.AssignmentId == assignmentId && m.StudentId == studentId);

            if (markingHeader == null)
                return NotFound("Marking record not found.");

            // ✅ Remove all associated MarkingDetails directly from DB
            //var detailsToRemove = _context.MarkingDetails
            // .Where(md => md.MarkingHeaderId == markingHeader.MarkingHeaderId);

            //_context.MarkingDetails.RemoveRange(detailsToRemove);

            // ✅ Reset header fields
            markingHeader.MarkingStatus = "draft";

            // Optionally reset other fields if needed (e.g., timestamps)
            // markingHeader.LastUpdated = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Marking progress has been fully reset (all scores cleared).";
            return RedirectToAction("Mark", new { assignmentId, studentId });
        }

        [HttpGet("GenerateReport/{assignmentId}/{studentId}")]
        public async Task<IActionResult> GenerateReport(string assignmentId, string studentId)
        {
            if (string.IsNullOrEmpty(assignmentId) || string.IsNullOrEmpty(studentId))
                return BadRequest("AssignmentId and StudentId are required.");

            // Load all data needed for the PDF
            var selectedAssignment = await _context.Assignments
                .Include(a => a.Course)
                .Include(a => a.Tasks)
                    .ThenInclude(t => t.Rubrics)
                        .ThenInclude(r => r.Criteria)
                .Include(a => a.Tasks)
                    .ThenInclude(t => t.Rubrics)
                        .ThenInclude(r => r.ScoreDefinitions)
                .FirstOrDefaultAsync(a => a.AssignmentId == assignmentId);

            if (selectedAssignment == null)
                return NotFound("Assignment not found.");

            var student = await _context.Students
                .FirstOrDefaultAsync(s => s.StudentId == studentId);

            if (student == null)
                return NotFound("Student not found.");

            var markingHeader = await _context.MarkingHeaders
                .Include(m => m.MarkingDetails)
                .FirstOrDefaultAsync(m => m.AssignmentId == assignmentId && m.StudentId == studentId);

            var scoreLevels = await _context.ScoreLevels.ToListAsync();

            var viewModel = new MarkingPageViewModel
            {
                Assignment = selectedAssignment,
                Student = student,
                MarkingHeader = markingHeader,
                ScoreLevels = scoreLevels
            };

            // Create score definitions map for quick lookup
            var scoreDefinitionsMap = new Dictionary<string, ScoreDefinition>();
            foreach (var task in selectedAssignment.Tasks)
            {
                foreach (var rubric in task.Rubrics)
                {
                    if (rubric.ScoreDefinitions != null)
                    {
                        foreach (var scoreDef in rubric.ScoreDefinitions)
                        {
                            if (!scoreDefinitionsMap.ContainsKey(scoreDef.ScoreDefinitionId))
                            {
                                scoreDefinitionsMap[scoreDef.ScoreDefinitionId] = scoreDef;
                            }
                        }
                    }
                }
            }

            // --- Call n8n to obtain AI feedback and include it into PDF ---
            N8nResponseRoot? finalFeedback = null;
            try
            {
                // Build payload (same structure used elsewhere)
                var allScoreLevels = await _context.ScoreLevels.ToListAsync();

                var payload = new WebhookPayload
                {
                    StudentName = student.FullName ?? student.StudentId,
                    RubricHeader = selectedAssignment.AssignmentName,
                    Question = "Automatically generated marking feedback.",
                    RubricList = new List<FeedbackRubricItem>()
                };

                // Build a lookup of ScoreDefinitionId -> ScoreDefinition for numeric values
                var sdMap = selectedAssignment.Tasks?
                    .SelectMany(t => t.Rubrics ?? Enumerable.Empty<Rubric>())
                    .SelectMany(r => r.ScoreDefinitions ?? Enumerable.Empty<ScoreDefinition>())
                    .ToDictionary(sd => sd.ScoreDefinitionId, sd => sd, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, ScoreDefinition>(StringComparer.OrdinalIgnoreCase);

                foreach (var task in selectedAssignment.Tasks ?? Enumerable.Empty<AIS_RubricFeedbackGenerator.Models.Task>())
                {
                    foreach (var rubric in task.Rubrics ?? Enumerable.Empty<Rubric>())
                    {
                        foreach (var criterion in rubric.Criteria ?? Enumerable.Empty<Criterion>())
                        {
                            var selectedDetail = markingHeader?.MarkingDetails?.FirstOrDefault(md => md.CriterionId == criterion.CriterionId);

                            double achievedScore = 0;
                            if (selectedDetail != null && !string.IsNullOrWhiteSpace(selectedDetail.ScoreDefinitionId))
                            {
                                if (sdMap.TryGetValue(selectedDetail.ScoreDefinitionId, out var sdFound))
                                    achievedScore = sdFound.ScoreValue;
                                else if (selectedDetail.ScoreLevel != null)
                                    achievedScore = selectedDetail.ScoreLevel.ScoreValue;
                            }

                            var scoreDefs = new List<FeedbackScoreDefinition>();
                            foreach (var sd in (rubric.ScoreDefinitions ?? Enumerable.Empty<ScoreDefinition>()))
                            {
                                var matchingLevel = allScoreLevels
                                    .FirstOrDefault(sl => sl.CriterionId == criterion.CriterionId && sl.ScoreDefinitionId == sd.ScoreDefinitionId);

                                var definitionText = matchingLevel?.Description;
                                if (string.IsNullOrWhiteSpace(definitionText))
                                {
                                    definitionText = sd.GetType().GetProperty("Definition") != null
                                        ? (string?)sd.GetType().GetProperty("Definition")!.GetValue(sd)
                                        : null;
                                }
                                if (string.IsNullOrWhiteSpace(definitionText))
                                    definitionText = sd.ScoreName ?? "";

                                scoreDefs.Add(new FeedbackScoreDefinition
                                {
                                    ScoreValue = sd.ScoreValue,
                                    ScoreName = sd.ScoreName,
                                    Definition = definitionText
                                });
                            }

                            payload.RubricList.Add(new FeedbackRubricItem
                            {
                                Criteria = criterion.Title,
                                AchievedScore = achievedScore,
                                ScoreDefinition = scoreDefs
                            });
                        }
                    }
                }

                var client = _httpClientFactory.CreateClient("n8n-webhook-prod");
                var response = await client.PostAsJsonAsync("", payload);
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("n8n response status {status}; content: {content}", response.StatusCode, responseContent);

                if (response.IsSuccessStatusCode)
                {
                    var feedbackArray = System.Text.Json.JsonSerializer.Deserialize<List<N8nResponseRoot>>(responseContent, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    finalFeedback = feedbackArray?.FirstOrDefault();
                }
                else
                {
                    _logger.LogWarning("n8n webhook responded with non-success status when generating PDF: {status}. Body: {body}", response.StatusCode, responseContent);
                    // finalFeedback remains null — we still generate the PDF but without AI feedback
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling n8n while generating PDF for {assignment}/{student}", assignmentId, studentId);
                // proceed without finalFeedback
            }

            // Generate PDF (pass optional n8n feedback)
            var pdfBytes = _pdfService.GenerateMarkingReportPdf(viewModel, scoreDefinitionsMap, finalFeedback);

            // Return PDF as download
            var fileName = $"{student.StudentId}_{selectedAssignment.CombinedAssignmentId}_{DateTime.Now:ddMMyy}.pdf";
            return File(pdfBytes, "application/pdf", fileName);
        }

        [HttpPost("TriggerN8NWorkflow")]
        public async Task<IActionResult> TriggerN8NWorkflow(string assignmentId, string studentId)
        {
            if (string.IsNullOrEmpty(assignmentId) || string.IsNullOrEmpty(studentId))
                return BadRequest("AssignmentId and StudentId are required.");

            var assignment = await _context.Assignments
                .Include(a => a.Course)
                .Include(a => a.Tasks)
                    .ThenInclude(t => t.Rubrics)
                        .ThenInclude(r => r.Criteria)
                .Include(a => a.Tasks)
                    .ThenInclude(t => t.Rubrics)
                        .ThenInclude(r => r.ScoreDefinitions)
                .FirstOrDefaultAsync(a => a.AssignmentId == assignmentId);

            if (assignment == null)
                return NotFound("Assignment not found.");

            var student = await _context.Students
                .FirstOrDefaultAsync(s => s.StudentId == studentId);

            if (student == null)
                return NotFound("Student not found.");

            var markingHeader = await _context.MarkingHeaders
                .Include(m => m.MarkingDetails)
                .ThenInclude(md => md.ScoreLevel)
                .FirstOrDefaultAsync(m => m.AssignmentId == assignmentId && m.StudentId == studentId);

            if (markingHeader == null)
                return BadRequest("No marking data found for this student.");

            // Build a lookup of ScoreDefinitionId -> ScoreDefinition for reliable numeric values
            var scoreDefinitionsMap = assignment.Tasks?
                .SelectMany(t => t.Rubrics ?? Enumerable.Empty<Rubric>())
                .SelectMany(r => r.ScoreDefinitions ?? Enumerable.Empty<ScoreDefinition>())
                .ToDictionary(sd => sd.ScoreDefinitionId, sd => sd, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, ScoreDefinition>(StringComparer.OrdinalIgnoreCase);

            // Preload score levels (still used for descriptions)
            var allScoreLevels = await _context.ScoreLevels.ToListAsync();

            var payload = new WebhookPayload
            {
                StudentName = student.FullName ?? student.StudentId,
                RubricHeader = assignment.AssignmentName,
                Question = "Automatically generated marking feedback.",
                RubricList = new List<FeedbackRubricItem>()
            };

            foreach (var task in assignment.Tasks ?? Enumerable.Empty<AIS_RubricFeedbackGenerator.Models.Task>())
            {
                foreach (var rubric in task.Rubrics ?? Enumerable.Empty<Rubric>())
                {
                    foreach (var criterion in rubric.Criteria ?? Enumerable.Empty<Criterion>())
                    {
                        var selectedDetail = markingHeader.MarkingDetails?
                            .FirstOrDefault(md => md.CriterionId == criterion.CriterionId);

                        // Prefer ScoreDefinition.ScoreValue if a ScoreDefinitionId was selected
                        double achievedScore = 0;
                        if (selectedDetail != null && !string.IsNullOrWhiteSpace(selectedDetail.ScoreDefinitionId))
                        {
                            if (scoreDefinitionsMap.TryGetValue(selectedDetail.ScoreDefinitionId, out var sdFound))
                            {
                                achievedScore = sdFound.ScoreValue;
                            }
                            else if (selectedDetail.ScoreLevel != null)
                            {
                                // fallback to ScoreLevel internal value (if present)
                                achievedScore = selectedDetail.ScoreLevel.ScoreValue;
                            }
                        }

                        var scoreDefs = new List<FeedbackScoreDefinition>();
                        foreach (var sd in (rubric.ScoreDefinitions ?? Enumerable.Empty<ScoreDefinition>()))
                        {
                            var matchingLevel = allScoreLevels
                                .FirstOrDefault(sl => sl.CriterionId == criterion.CriterionId && sl.ScoreDefinitionId == sd.ScoreDefinitionId);

                            var definitionText = matchingLevel?.Description;
                            if (string.IsNullOrWhiteSpace(definitionText))
                            {
                                definitionText = sd.GetType().GetProperty("Definition") != null
                                    ? (string?)sd.GetType().GetProperty("Definition")!.GetValue(sd)
                                    : null;
                            }
                            if (string.IsNullOrWhiteSpace(definitionText))
                                definitionText = sd.ScoreName ?? "";

                            scoreDefs.Add(new FeedbackScoreDefinition
                            {
                                ScoreValue = sd.ScoreValue,
                                ScoreName = sd.ScoreName,
                                Definition = definitionText
                            });
                        }

                        payload.RubricList.Add(new FeedbackRubricItem
                        {
                            Criteria = criterion.Title,
                            AchievedScore = achievedScore,
                            ScoreDefinition = scoreDefs
                        });
                    }
                }
            }

            // Determine rubric id to store on header
            var rubricFromFirstCriterion = assignment.Tasks?
                .SelectMany(t => t.Rubrics ?? Enumerable.Empty<Rubric>())
                .SelectMany(r => r.Criteria ?? Enumerable.Empty<Criterion>())
                .FirstOrDefault();
            string? rubricIdToUse = rubricFromFirstCriterion?.RubricId
                ?? assignment.Tasks?
                    .SelectMany(t => t.Rubrics ?? Enumerable.Empty<Rubric>())
                    .Select(r => r.RubricId)
                    .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(rubricIdToUse))
                return BadRequest("Cannot create feedback: assignment contains no rubrics.");

            var client = _httpClientFactory.CreateClient("n8n-webhook-prod");

            try
            {
                try { _logger.LogDebug("n8n payload: {payload}", System.Text.Json.JsonSerializer.Serialize(payload)); } catch { }

                var response = await client.PostAsJsonAsync("", payload);
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("n8n response status {status}; content: {content}", response.StatusCode, responseContent);

                if (!response.IsSuccessStatusCode)
                    return StatusCode((int)response.StatusCode, $"Webhook failed: {responseContent}");

                var feedbackArray = System.Text.Json.JsonSerializer.Deserialize<List<N8nResponseRoot>>(responseContent, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var finalFeedback = feedbackArray?.FirstOrDefault();
                if (finalFeedback == null)
                    return BadRequest("Invalid response from n8n.");

                // Save FeedbackHeader
                string yearPrefix = DateTime.Now.ToString("yy");
                var lastFH = await _context.FeedbackHeader
                    .Where(r => r.FeedbackHeaderId.StartsWith($"FH{yearPrefix}"))
                    .OrderByDescending(r => r.FeedbackHeaderId)
                    .FirstOrDefaultAsync();

                int lastNum = lastFH != null && lastFH.FeedbackHeaderId.Length >= 6
                    ? int.Parse(lastFH.FeedbackHeaderId.Substring(4))
                    : 0;

                string newHeaderId = $"FH{yearPrefix}{(lastNum + 1):D4}";

                var header = new FeedbackHeader
                {
                    FeedbackHeaderId = newHeaderId,
                    RubricId = rubricIdToUse,
                    MarkingHeaderId = markingHeader.MarkingHeaderId,
                    AchievedScore = finalFeedback.AchievedScore,
                    MaxScore = finalFeedback.MaxScore,
                    SummaryFeedback = finalFeedback.SummaryFeedback,
                    Encouragement = finalFeedback.Encouragement,
                    CreatedAt = DateTime.Now
                };

                _context.FeedbackHeader.Add(header);

                var lastFD = await _context.FeedbackDetail
                    .Where(r => r.FeedbackDetailId.StartsWith($"FD{yearPrefix}"))
                    .OrderByDescending(r => r.FeedbackDetailId)
                    .FirstOrDefaultAsync();
                int lastFDNum = lastFD != null && lastFD.FeedbackDetailId.Length >= 6
                    ? int.Parse(lastFD.FeedbackDetailId.Substring(4))
                    : 0;

                int idx = 1;
                var criterionNameToId = assignment.Tasks?
                    .SelectMany(t => t.Rubrics ?? Enumerable.Empty<Rubric>())
                    .SelectMany(r => r.Criteria ?? Enumerable.Empty<Criterion>())
                    .Where(c => !string.IsNullOrWhiteSpace(c.Title))
                    .ToDictionary(c => c.Title.Trim(), c => c.CriterionId, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var fc in finalFeedback.FeedbackCriteria ?? Enumerable.Empty<FeedbackCriterion>())
                {
                    string? mappedCriterionId = null;
                    if (!string.IsNullOrWhiteSpace(fc.CriterionName) &&
                        criterionNameToId.TryGetValue(fc.CriterionName.Trim(), out var mappedId))
                    {
                        mappedCriterionId = mappedId;
                    }

                    var relatedDetail = mappedCriterionId != null
                        ? markingHeader.MarkingDetails?.FirstOrDefault(md => md.CriterionId == mappedCriterionId)
                        : null;

                    var finalCriterionId = mappedCriterionId ?? relatedDetail?.CriterionId;
                    if (string.IsNullOrWhiteSpace(finalCriterionId))
                    {
                        idx++;
                        continue;
                    }

                    string newDetailId = $"FD{yearPrefix}{(lastFDNum + idx):D4}";

                    var detail = new FeedbackDetail
                    {
                        FeedbackDetailId = newDetailId,
                        FeedbackHeaderId = newHeaderId,
                        CriterionId = finalCriterionId,
                        MarkingDetailId = relatedDetail?.MarkingDetailId,
                        AchievedScore = fc.AchievedScore,
                        MaxScore = fc.MaxScore,
                        FeedbackParagraph = fc.FeedbackParagraph,
                        SuggestionParagraph = fc.Suggestions,
                        CreatedAt = DateTime.Now
                    };

                    _context.FeedbackDetail.Add(detail);
                    idx++;
                }

                await _context.SaveChangesAsync();

                return Ok($"Webhook processed and feedback stored. Student: {finalFeedback.StudentName}, Score: {finalFeedback.AchievedScore}/{finalFeedback.MaxScore}");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP Request Error while triggering n8n workflow.");
                return StatusCode(500, $"Error sending webhook: {ex.Message}");
            }
        }
    }
}
