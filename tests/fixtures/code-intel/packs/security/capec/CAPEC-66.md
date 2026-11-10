# CAPEC-66: SQL Injection

An attack pattern of MITRE's CAPEC 3.9: Standard abstraction, Draft. Likelihood of attack: High. Typical severity: High.

This attack exploits target software that constructs SQL statements based on user input. An attacker crafts input strings so that when the target software constructs SQL statements based on the input, the resulting SQL statement performs actions other than those the application intended. SQL Injection results from failure of the application to appropriately validate input.

When specially crafted user-controlled input consisting of SQL syntax is used without proper validation as part of SQL queries, it is possible to glean information from the database in ways not envisaged during application design. Depending upon the database and the design of the application, it may also be possible to leverage injection to have the database execute system-related commands of the attackers' choice. SQL Injection enables an attacker to interact directly to the database, thus bypassing the application completely. Successful injection can cause information disclosure as well as ability to add or modify data in the database.

## Execution flow

1. **Explore**: [Survey application] The attacker first takes an inventory of the functionality exposed by the application.
   - Spider web sites for all available links
   - Sniff network communications with application using a utility such as WireShark.
2. **Experiment**: [Determine user-controllable input susceptible to injection] Determine the user-controllable input susceptible to injection. For each user-controllable input that the attacker suspects is vulnerable to SQL injection, attempt to inject characters that have special meaning in SQL (such as a single quote character, a double quote character, two hyphens, a parenthesis, etc.). The goal is to create a SQL query with an invalid syntax.
   - Use web browser to inject input through text fields or through HTTP GET parameters.
   - Use a web application debugging tool such as Tamper Data, TamperIE, WebScarab,etc. to modify HTTP POST parameters, hidden fields, non-freeform fields, etc.
   - Use network-level packet injection tools such as netcat to inject input
   - Use modified client (modified by reverse engineering) to inject input.
3. **Experiment**: [Experiment with SQL Injection vulnerabilities] After determining that a given input is vulnerable to SQL Injection, hypothesize what the underlying query looks like. Iteratively try to add logic to the query to extract information from the database, or to modify or delete information in the database.
   - Use public resources such as "SQL Injection Cheat Sheet" at http://ferruh.mavituna.com/makale/sql-injection-cheatsheet/, and try different approaches for adding logic to SQL queries.
   - Add logic to query, and use detailed error messages from the server to debug the query. For example, if adding a single quote to a query causes an error message, try : "' OR 1=1; --", or something else that would syntactically complete a hypothesized query. Iteratively refine the query.
   - Use "Blind SQL Injection" techniques to extract information about the database schema.
   - If a denial of service attack is the goal, try stacking queries. This does not work on all platforms (most notably, it does not work on Oracle or MySQL). Examples of inputs to try include: "'; DROP TABLE SYSOBJECTS; --" and "'); DROP TABLE SYSOBJECTS; --". These particular queries will likely not work because the SYSOBJECTS table is generally protected.
4. **Exploit**: [Exploit SQL Injection vulnerability] After refining and adding various logic to SQL queries, craft and execute the underlying SQL query that will be used to attack the target system. The goal is to reveal, modify, and/or delete database data, using the knowledge obtained in the previous step. This could entail crafting and executing multiple SQL queries if a denial of service attack is the intent.
   - Craft and Execute underlying SQL query

## Prerequisites

- SQL queries used by the application to store, retrieve or modify data.
- User-controllable input that is not properly validated by the application as part of SQL queries.

## Indicators

- Too many false or invalid queries to the database, especially those caused by malformed input.

## Resources required

- None: No specialized resources are required to execute this type of attack.

## Skills required

- **Low**: It is fairly simple for someone with basic SQL knowledge to perform SQL injection, in general. In certain instances, however, specific knowledge of the database employed may be required.

## Consequences

- **Integrity**: Modify Data.
- **Confidentiality**: Read Data.
- **Confidentiality, Integrity, Availability**: Execute Unauthorized Commands. Run Arbitrary Code
- **Confidentiality, Access Control, Authorization**: Gain Privileges.

## Mitigations

- Strong input validation - All user-controllable input must be validated and filtered for illegal characters as well as SQL content. Keywords such as UNION, SELECT or INSERT must be filtered in addition to characters such as a single-quote(') or SQL-comments (--) based on the context in which they appear.
- Use of parameterized queries or stored procedures - Parameterization causes the input to be restricted to certain domains, such as strings or integers, and any input outside such domains is considered invalid and the query fails. Note that SQL Injection is possible even in the presence of stored procedures if the eventual query is constructed dynamically.
- Use of custom error pages - Attackers can glean information about the nature of queries from descriptive error messages. Input validation must be coupled with customized error pages that inform about an error without disclosing information about the database or application.

## Example instances

With PHP-Nuke versions 7.9 and earlier, an attacker can successfully access and modify data, including sensitive contents such as usernames and password hashes, and compromise the application through SQL Injection. The protection mechanism against SQL Injection employs a denylist approach to input validation. However, because of an improper denylist, it is possible to inject content such as "foo'/**/UNION" or "foo UNION/**/" to bypass validation and glean sensitive information from the database. See also: CVE-2006-5525


## Related weaknesses

CWE-89, CWE-1286.

## Related attack patterns

ChildOf CAPEC-248.

---
Source: MITRE's CAPEC (Common Attack Pattern Enumeration and Classification), used under MITRE's terms of use. https://capec.mitre.org/data/definitions/66.html
