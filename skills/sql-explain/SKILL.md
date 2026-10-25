---
name: sql-explain
title: Explain and speed up a SQL query
description: Explain what a SQL query does, check it is correct, and make it faster — indexes, rewrites, the plan. Use when given a SQL query that is slow, wrong or hard to read.
parameters:
  - { name: database, description: "The database engine", required: false, default: "PostgreSQL" }
---
Take the SQL query given ($ARGUMENTS), for {{database}}.

1. **Explain** what it returns, in plain words, clause by clause.
2. **Check** correctness: joins that multiply rows, NULL handling, GROUP BY mistakes, time zones, off-by-one ranges.
3. **Speed it up**: read its plan (EXPLAIN ANALYZE when it can be run), then propose indexes (exact DDL), rewrites (sargable predicates, EXISTS over IN, CTEs that block pushdown, window functions), and pagination by keyset instead of OFFSET when relevant.
4. Give the improved query, and say what each change gains and costs (write overhead of an index, for instance).

Never run statements that change data unless asked.
