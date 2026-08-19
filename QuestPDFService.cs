using AIS_RubricFeedbackGenerator.Models;
using QuestPDF.Companion;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AIS_RubricFeedbackGenerator.Services
{
    public class QuestPDFService
    {
        List<RubricFormViewModel> rubrics = new List<RubricFormViewModel>();
        Student student = new Student
        {
            FullName = "Kevin Wikasa",
            StudentId = "20242688"
        };

        public void GenerateReportPdf(List<RubricFormViewModel> model)
        {
            rubrics = model ?? new List<RubricFormViewModel>();
            QuestPDF.Settings.License = LicenseType.Community;
            var document = Compose();
            document.ShowInCompanion();
        }

        private IDocument Compose()
        {
            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Content().Element(ComposeContent);
                    page.Footer().Element(ComposeFooter);
                });
            });
            return document;
        }

        private void ComposeContent(IContainer container)
        {
            container.PaddingVertical(40).Column(column =>
            {
                column.Item()
                        .Text(student.FullName)
                        .FontSize(20).SemiBold().FontColor(Colors.Blue.Medium);

                column.Item().Text(text =>
                {
                    text.Span("Student ID: ").SemiBold().FontSize(14);
                    text.Span(student.StudentId).FontSize(14);
                });

                column.Item().Text(text =>
                {
                    text.Span("Issue date: ").SemiBold().FontSize(14);
                    text.Span($"{DateTime.Now:d}").FontSize(14);
                });
                column.Spacing(5);
                column.Item().Element(ComposeSummary);

                foreach (var rubric in rubrics ?? Enumerable.Empty<RubricFormViewModel>())
                {
                    column.Item().PaddingTop(25).Element(itemContainer =>
                    {
                        ComposeRubricTable(rubric, itemContainer);
                    });
                }
            });
        }

        private void ComposeRubricTable(RubricFormViewModel rubric, IContainer container)
        {
            var criteria = rubric?.Criteria ?? new List<CriterionInputModel>();
            var scoreDefs = rubric?.ScoreDefinitions ?? new List<ScoreDefinitionInputModel>();
            var levelDescriptions = rubric?.ScoreLevelDescriptions ?? new List<List<string>>();

            container.Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn();
                    foreach (var score in scoreDefs)
                        columns.RelativeColumn();
                });

                table.Header(header =>
                {
                    header.Cell().Element(CellStyle).Text(rubric?.RubricTitle ?? "");
                    foreach (var score in scoreDefs)
                    {
                        header.Cell().Element(CellStyle).Text($"{score.ScoreValue} - {score.ScoreName}");
                    }

                    static IContainer CellStyle(IContainer container)
                    {
                        return container.DefaultTextStyle(x => x.SemiBold()).PaddingVertical(5).BorderBottom(1).BorderColor(Colors.Black);
                    }
                });

                for (int c = 0; c < criteria.Count; c++)
                {
                    var crit = criteria[c];
                    table.Cell().Element(CellStyle).Text(crit.Title ?? "").FontSize(10);

                    var descriptionsRow = (c < levelDescriptions.Count) ? levelDescriptions[c] : new List<string>();
                    for (int i = 0; i < scoreDefs.Count; i++)
                    {
                        var levelText = (i < descriptionsRow.Count) ? descriptionsRow[i] : "-";
                        table.Cell().Element(CellStyle).Text(levelText).FontSize(10);
                    }

                    static IContainer CellStyle(IContainer container)
                    {
                        return container.BorderBottom(1).BorderVertical(1).BorderColor(Colors.Grey.Lighten2).PaddingVertical(5);
                    }
                }
            });
        }

        private void ComposeSummary(IContainer container)
        {
            container.Background(Colors.Grey.Lighten3).Padding(10).Column(column =>
            {
                column.Spacing(5);
                column.Item().Text("Summary").FontSize(14);
                column.Item().Text(Placeholders.LoremIpsum()).FontSize(12).Justify();
            });
        }

        private void ComposeFooter(IContainer container)
        {
            // optional footer content
        }

        public byte[] GenerateMarkingReportPdf(MarkingPageViewModel viewModel, Dictionary<string, ScoreDefinition> scoreDefinitionsMap, N8nResponseRoot? n8nFeedback = null)
        {
            if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));
            QuestPDF.Settings.License = LicenseType.Community;
            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(10));
                    page.Content().Element(container => ComposeMarkingContent(container, viewModel, scoreDefinitionsMap ?? new Dictionary<string, ScoreDefinition>(), n8nFeedback));
                    page.Footer().Element(ComposeFooter);
                });
            });

            return document.GeneratePdf();
        }

        private void ComposeMarkingContent(IContainer container, MarkingPageViewModel viewModel, Dictionary<string, ScoreDefinition> scoreDefinitionsMap, N8nResponseRoot? n8nFeedback)
        {
            var assignment = viewModel.Assignment;
            var tasks = assignment?.Tasks ?? Enumerable.Empty<AIS_RubricFeedbackGenerator.Models.Task>();
            var markingHeader = viewModel.MarkingHeader;
            var scoreLevels = viewModel.ScoreLevels ?? new List<ScoreLevel>();

            // build criterion title -> task title lookup (used to indicate which task each criterion feedback belongs to)
            var criterionTitleToTaskTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (assignment?.Tasks != null)
            {
                foreach (var task in assignment.Tasks)
                {
                    var taskTitle = task?.Title?.Trim() ?? "-";
                    foreach (var rubric in task?.Rubrics ?? Enumerable.Empty<Rubric>())
                    {
                        foreach (var criterion in rubric?.Criteria ?? Enumerable.Empty<Criterion>())
                        {
                            var critTitle = criterion?.Title?.Trim();
                            if (!string.IsNullOrWhiteSpace(critTitle) && !criterionTitleToTaskTitle.ContainsKey(critTitle))
                                criterionTitleToTaskTitle[critTitle] = taskTitle;
                        }
                    }
                }
            }

            container.PaddingVertical(40).Column(column =>
            {
                column.Item().Text("MARKING REPORT").FontSize(24).SemiBold().FontColor(Colors.Blue.Medium).AlignCenter();
                column.Item().Height(20);

                column.Item().Background(Colors.Grey.Lighten4).Padding(15).Column(infoColumn =>
                {
                    infoColumn.Item().Text("Student Information").FontSize(16).SemiBold().FontColor(Colors.Blue.Darken2);
                    infoColumn.Item().PaddingVertical(5);

                    infoColumn.Item().Row(row =>
                    {
                        row.ConstantItem(150).Text("Course:").SemiBold().FontSize(12);
                        row.RelativeItem().Text(assignment?.Course?.CourseName ?? "-").FontSize(12);
                    });

                    infoColumn.Item().Row(row =>
                    {
                        row.ConstantItem(150).Text("Assessment:").SemiBold().FontSize(12);
                        row.RelativeItem().Text(assignment?.AssignmentName ?? "-").FontSize(12);
                    });

                    infoColumn.Item().Row(row =>
                    {
                        row.ConstantItem(150).Text("Student ID:").SemiBold().FontSize(12);
                        row.RelativeItem().Text(viewModel.Student?.StudentId ?? "-").FontSize(12);
                    });

                    infoColumn.Item().Row(row =>
                    {
                        row.ConstantItem(150).Text("Student Name:").SemiBold().FontSize(12);
                        row.RelativeItem().Text(viewModel.Student?.FullName ?? "-").FontSize(12);
                    });

                    infoColumn.Item().Row(row =>
                    {
                        row.ConstantItem(150).Text("Assessment Year:").SemiBold().FontSize(12);
                        row.RelativeItem().Text(assignment?.TrimesterYear?.ToString() ?? "-").FontSize(12);
                    });

                    infoColumn.Item().Row(row =>
                    {
                        row.ConstantItem(150).Text("Assessment Trimester:").SemiBold().FontSize(12);
                        row.RelativeItem().Text(assignment?.AssignmentTrimester?.ToString() ?? "-").FontSize(12);
                    });

                    infoColumn.Item().Row(row =>
                    {
                        row.ConstantItem(150).Text("Report Date:").SemiBold().FontSize(12);
                        row.RelativeItem().Text($"{DateTime.Now:dd/MM/yyyy}").FontSize(12);
                    });
                });

                column.Item().Height(20);

                if (n8nFeedback != null)
                {
                    column.Item().Background(Colors.Grey.Lighten3).Padding(12).Column(fb =>
                    {
                        // Title
                        fb.Item().Text(text => { text.Span("AI Generated Feedback").SemiBold().FontSize(16); });

                        // Summary
                        if (!string.IsNullOrWhiteSpace(n8nFeedback.SummaryFeedback))
                        {
                            fb.Item().PaddingBottom(4).Text(text =>
                            {
                                text.Span("Summary: ").SemiBold().FontSize(11);
                                text.Span(n8nFeedback.SummaryFeedback ?? "").FontSize(11);
                            });
                        }

                        // Encouragement
                        if (!string.IsNullOrWhiteSpace(n8nFeedback.Encouragement))
                        {
                            fb.Item().PaddingBottom(4).Text(text =>
                            {
                                text.Span("Encouragement: ").SemiBold().FontSize(11);
                                text.Span(n8nFeedback.Encouragement ?? "").FontSize(11);
                            });
                        }

                        // Per-criterion feedback (keep existing feedback rendering, but print task title only once per task)
                        if (n8nFeedback.FeedbackCriteria != null && n8nFeedback.FeedbackCriteria.Any())
                        {
                            fb.Item().PaddingTop(6).Text(text => text.Span("Per-criterion feedback:").SemiBold().FontSize(12));

                            // Track which task titles we've already shown
                            var printedTaskTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                            foreach (var fc in n8nFeedback.FeedbackCriteria)
                            {
                                // show task title (if mapped) only once for the first criterion of that task
                                fb.Item().PaddingBottom(6).Column(c =>
                                {
                                    var criterionName = fc.CriterionName?.Trim();
                                    if (!string.IsNullOrWhiteSpace(criterionName) && criterionTitleToTaskTitle.TryGetValue(criterionName, out var mappedTaskTitle))
                                    {
                                        if (!printedTaskTitles.Contains(mappedTaskTitle))
                                        {
                                            // Print task title header once
                                            c.Item().Text(text =>
                                            {
                                                text.Span("Task: ").SemiBold().FontSize(10);
                                                text.Span(mappedTaskTitle).FontSize(10);
                                            });

                                            printedTaskTitles.Add(mappedTaskTitle);
                                        }
                                    }

                                    // Criterion title
                                    c.Item().Text(text => text.Span(fc.CriterionName ?? "-").SemiBold().FontSize(11));

                                    // Feedback paragraph — use container padding then chain Text(string)
                                    c.Item().PaddingLeft(6).PaddingBottom(4)
                                        .Text(fc.FeedbackParagraph ?? "")
                                        .FontSize(10);

                                    // Suggestions
                                    if (!string.IsNullOrWhiteSpace(fc.Suggestions))
                                    {
                                        c.Item().Text("Suggestions:").SemiBold().FontSize(10);
                                        c.Item().PaddingLeft(6)
                                            .Text(fc.Suggestions).FontSize(10);
                                    }
                                });
                            }
                        }
                    });
                }

                column.Item().Height(10);

                foreach (var task in tasks)
                {
                    column.Item().PaddingTop(15);
                    column.Item().Text($"Task: {task.Title}").FontSize(18).SemiBold().FontColor(Colors.Blue.Medium);

                    foreach (var rubric in task.Rubrics ?? Enumerable.Empty<Rubric>())
                    {
                        ComposeMarkingRubricTable(rubric, viewModel, scoreDefinitionsMap, column);
                    }
                }

                if (markingHeader?.MarkingDetails != null && markingHeader.MarkingDetails.Any())
                {
                    column.Item().Height(30);
                    column.Item().Background(Colors.Green.Lighten4).Padding(15).Column(summaryColumn =>
                    {
                        summaryColumn.Item().Text("TOTAL SCORE SUMMARY").FontSize(16).SemiBold().FontColor(Colors.Blue.Darken2);

                        double totalScore = 0;
                        double maxPossible = 0;

                        foreach (var task in tasks)
                        {
                            foreach (var rubric in task.Rubrics ?? Enumerable.Empty<Rubric>())
                            {
                                foreach (var criterion in rubric.Criteria ?? Enumerable.Empty<Criterion>())
                                {
                                    var maxScoreDef = (rubric.ScoreDefinitions ?? Enumerable.Empty<ScoreDefinition>()).OrderByDescending(sd => sd.ScoreValue).FirstOrDefault();
                                    if (maxScoreDef != null)
                                    {
                                        maxPossible += maxScoreDef.ScoreValue;
                                    }

                                    var markingDetail = markingHeader.MarkingDetails?.FirstOrDefault(md => md.CriterionId == criterion.CriterionId);
                                    if (markingDetail != null && !string.IsNullOrEmpty(markingDetail.ScoreDefinitionId))
                                    {
                                        totalScore += GetScoreDefinitionValue(scoreDefinitionsMap, markingDetail.ScoreDefinitionId);
                                    }
                                }
                            }
                        }

                        summaryColumn.Item().Row(row =>
                        {
                            row.ConstantItem(200).Text("Total Marked Score:").SemiBold().FontSize(14);
                            row.RelativeItem().Text($"{totalScore:F2}").FontSize(14).SemiBold();
                        });

                        summaryColumn.Item().Row(row =>
                        {
                            row.ConstantItem(200).Text("Maximum Possible Score:").SemiBold().FontSize(14);
                            row.RelativeItem().Text($"{maxPossible:F2}").FontSize(14);
                        });

                        summaryColumn.Item().Row(row =>
                        {
                            row.ConstantItem(200).Text("Percentage:").SemiBold().FontSize(14);
                            var percentage = maxPossible > 0 ? (totalScore / maxPossible * 100) : 0;
                            row.RelativeItem().Text($"{percentage:F2}%").FontSize(14).SemiBold().FontColor(percentage >= 50 ? Colors.Green.Darken2 : Colors.Red.Darken2);
                        });
                    });
                }
            });
        }

        private static double GetScoreDefinitionValue(Dictionary<string, ScoreDefinition> map, string key)
        {
            if (map == null || string.IsNullOrWhiteSpace(key)) return 0;
            return map.TryGetValue(key, out var sd) ? sd.ScoreValue : 0;
        }

        private void ComposeMarkingRubricTable(Rubric rubric, MarkingPageViewModel viewModel, Dictionary<string, ScoreDefinition> scoreDefinitionsMap, ColumnDescriptor parentColumn)
        {
            parentColumn.Item().PaddingTop(10).PaddingVertical(10).Column(rubricColumn =>
            {
                rubricColumn.Item().Background(Colors.Blue.Lighten5).Padding(10).Text(rubric.Question).FontSize(14).SemiBold();

                var scoreDefs = (rubric.ScoreDefinitions ?? new List<ScoreDefinition>())
                    .OrderByDescending(sd => sd.ScoreValue)
                    .ToList();

                rubricColumn.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(160);
                        foreach (var _ in scoreDefs)
                        {
                            columns.RelativeColumn();
                        }
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(container =>
                        {
                            var styled = CellHeaderStyle(container);
                            styled.Text("Criteria").SemiBold().FontSize(11);
                            return styled;
                        });

                        foreach (var scoreDef in scoreDefs)
                        {
                            header.Cell().Element(container =>
                            {
                                var styled = CellHeaderStyle(container);
                                styled.Text($"{scoreDef.ScoreValue}\n{scoreDef.ScoreName}")
                                    .FontSize(10)
                                    .AlignCenter();
                                return styled;
                            });
                        }
                    });

                    foreach (var criterion in rubric.Criteria ?? Enumerable.Empty<Criterion>())
                    {
                        table.Cell().Element(container =>
                        {
                            var styled = CellStyle(container);
                            styled.Text(criterion.Title ?? "").FontSize(10);
                            return styled;
                        });

                        foreach (var scoreDef in scoreDefs)
                        {
                            var isSelected = viewModel.MarkingHeader?.MarkingDetails?
                                .Any(md => md.CriterionId == criterion.CriterionId && md.ScoreDefinitionId == scoreDef.ScoreDefinitionId) ?? false;

                            var scoreLevel = viewModel.ScoreLevels?
                                .FirstOrDefault(sl => sl.CriterionId == criterion.CriterionId && sl.ScoreDefinitionId == scoreDef.ScoreDefinitionId);

                            table.Cell().Element(container =>
                            {
                                var styled = CellStyle(container, isSelected);
                                styled.Text(scoreLevel?.Description ?? "-")
                                    .FontSize(9)
                                    .FontColor(isSelected ? Colors.White : Colors.Grey.Darken2)
                                    .AlignCenter();
                                return styled;
                            });
                        }
                    }

                    static IContainer CellStyle(IContainer container, bool isSelected = false)
                    {
                        var baseContainer = container
                            .BorderBottom(1)
                            .BorderColor(Colors.Grey.Lighten2)
                            .Padding(6);

                        return isSelected
                            ? baseContainer.Background(Colors.Green.Lighten2).Border(0)
                            : baseContainer.Background(Colors.White);
                    }

                    static IContainer CellHeaderStyle(IContainer container)
                    {
                        return container.Background(Colors.Blue.Medium)
                            .Padding(6)
                            .DefaultTextStyle(x => x.FontColor(Colors.White));
                    }
                });
            });
        }
    }
}