# Ask your data

People ask the company's databases in words. The model writes the SQL. The
app checks it and runs it read only. The answer shows what the SQL means,
with a chart and the rows. Charts worth keeping go onto boards, which run
again on their own schedules and can be shared.

## Asking

**Workspace → Data.**

1. Choose a database: those an admin opened to you.
2. Ask, for example: "How many orders did we have each week this quarter?"

**Tables** lists what can be answered: the tables open to questions, their
columns and what they hold.

Each answer has:

- **What it means.** One or two plain sentences about what the SQL counts
  or lists.
- **The chart.** One figure for a single number, lines for change over
  time, bars to compare a few groups, a table for a list. **Chart** and
  **Table** switch between the two views. A chart has one value axis, at
  most eight series, and the first 50 bars; the table has every row.
- **SQL.** The query as it ran. Change it and press **Run** to run your
  version: the same checks apply.
- **Ask a follow-up.** The next question builds on this one ("and by
  country?"): the model sees the question and the SQL before it.
- **Pin to a board.** Puts the chart on one of your boards, or on a new
  one, with how often it runs again: every hour, every day, or only when
  you refresh it.

A question is asked as you: your default model, your credit and your
safeguards (the question's rules before, the answer's after). When the SQL
fails, the model reads why and writes it once more. If the second try fails
too, the answer shows the error and the SQL, for you to fix by hand. When
the tables cannot answer the question, the model says so instead of
guessing.

In a chat, the **Company data** tool does the same. The model lists the
databases you may ask (`data_tables`) and runs one query at a time
(`data_query`). It sees at most the first 50 rows of a result.

## Boards

**Data → Boards** lists the boards you may see: yours, your groups' and
everyone's.

- A board shows each chart as it last ran, with when that was.
- Its owner can **Run it now**, change how often a chart runs again, take a
  chart off the board, and **Change** the board: its name, its words, and
  who may see it (**Only me**, **Some groups**, **Everyone**). Groups are
  the ones you are in, unless you may share with any group.
- **Post to a room**: the chart's last result goes to a room you are in,
  as a table, from you, for the room's people (not to the model).

A board's charts always run as its owner. People you share it with see the
results of what the owner may ask, as with a shared answer. When the owner
may no longer ask a chart's database, the chart says so and keeps its last
result.

## Connecting a database (admins)

**Administration → Data sources** needs the `data.manage` permission.

First, make a PostgreSQL role that may only read what people should ask,
for example:

```sql
create role arena_reader login password '…';
grant usage on schema shop to arena_reader;
grant select on shop.orders, shop.customers to arena_reader;
```

Then:

1. Press **Connect a database** and give a name.
2. Give the connection, for example
   `Host=db.example.com;Port=5432;Database=shop;Username=arena_reader;Password=…`.
   It is kept encrypted and never shown again: the page shows only who it
   signs in as and where.
3. Press **Try it** to list the tables and views the role may read.
4. Tick the tables that are **open to questions**. Nothing else is read,
   even when the role may read more.
5. Add **What the model should know**: units, what a status means, which
   dates count. The model reads this with every question.
6. Choose **Who may ask it**: everyone, or some groups.
7. Set the limits: the most rows an answer brings back (1,000 by default)
   and how many seconds a query may run (15).

**Read its tables again** picks up a new column or table. The tables you
opened stay as they were. **What was asked** lists the latest 100 queries:
who ran them, the question, the SQL, the row count, how long each took,
and any error. Removing a source also removes the charts pinned from it.

### What keeps it read only

Each query:

- runs on a connection of its own (no pool), in a `READ ONLY` transaction
  with a statement timeout and a lock timeout, and is never committed;
- is one statement: it goes to PostgreSQL as a single prepared statement,
  which the server refuses if there is more than one, so nothing can slip
  a `COMMIT` and a write after it;
- must start with `SELECT`, `WITH`, `VALUES` or `TABLE`;
- is checked against its plan (`EXPLAIN`) before it runs. Every table it
  reads must be open to questions. A view that is open lets its own tables
  be read through it. A function it scans must be one of the database's
  own (`pg_catalog`).

The connection is also refused when its role is a superuser, or may read
the server's files or run its programs. **Try it** warns when the role may
change tables. Every query runs read only anyway, but a role that may only
read is the surer guard. A function called inside a query runs with the
role's own rights, so give the role nothing it should not read.

People need the `feature.data` permission to ask a database or see a board.
Everyone holds it unless an admin denies it. The chat's **Company data**
tool is off in new chats until an admin turns it on (Admin → Tools).

Only PostgreSQL can be connected for now. MySQL and SQL Server need drivers
the app does not ship yet.
