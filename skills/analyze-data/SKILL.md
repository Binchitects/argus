---
name: analyze-data
title: Analyse data
description: Analyse a dataset (a spreadsheet, CSV or table) to answer a question — clean it, compute, chart, and say what it shows with its limits. Use when given data and asked what it shows, for trends, comparisons or a report.
parameters:
  - { name: question, description: "What to find out from the data", required: false, default: "what the data shows that matters" }
---
Analyse the data given to answer: {{question}}.

1. **Look first**: its columns, types, size, missing values, duplicates, outliers. Say what you clean and how.
2. **Compute** with code (Python, when available) rather than by eye: the figures that answer the question, with their units.
3. **Chart** the main finding (one clear chart beats several), with labelled axes and a title that states the finding.
4. **Conclude**: the answer in two or three sentences, the evidence for it, and its limits (sample size, bias, what correlation does not prove).
5. Suggest the next question worth asking.

Show the code you ran, briefly.
