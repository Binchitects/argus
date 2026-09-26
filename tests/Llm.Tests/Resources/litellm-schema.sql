-- The gateway's tables as a running LiteLLM creates them (pg_dump --schema-only),
-- without foreign keys to tables the tests do not need.

CREATE TABLE public."LiteLLM_SpendLogs" (
    request_id text NOT NULL,
    call_type text NOT NULL,
    api_key text DEFAULT ''::text NOT NULL,
    spend double precision DEFAULT 0.0 NOT NULL,
    total_tokens integer DEFAULT 0 NOT NULL,
    prompt_tokens integer DEFAULT 0 NOT NULL,
    completion_tokens integer DEFAULT 0 NOT NULL,
    "startTime" timestamp(3) without time zone NOT NULL,
    "endTime" timestamp(3) without time zone NOT NULL,
    "completionStartTime" timestamp(3) without time zone,
    model text DEFAULT ''::text NOT NULL,
    model_id text DEFAULT ''::text,
    model_group text DEFAULT ''::text,
    custom_llm_provider text DEFAULT ''::text,
    api_base text DEFAULT ''::text,
    "user" text DEFAULT ''::text,
    metadata jsonb DEFAULT '{}'::jsonb,
    cache_hit text DEFAULT ''::text,
    cache_key text DEFAULT ''::text,
    request_tags jsonb DEFAULT '[]'::jsonb,
    team_id text,
    end_user text,
    requester_ip_address text,
    messages jsonb DEFAULT '{}'::jsonb,
    response jsonb DEFAULT '{}'::jsonb,
    proxy_server_request jsonb DEFAULT '{}'::jsonb,
    session_id text,
    status text,
    mcp_namespaced_tool_name text,
    organization_id text,
    agent_id text,
    request_duration_ms integer,
    created_at timestamp(3) without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    updated_at timestamp(3) without time zone DEFAULT CURRENT_TIMESTAMP NOT NULL
);

CREATE TABLE public."LiteLLM_VerificationToken" (
    token text NOT NULL,
    key_name text,
    key_alias text,
    soft_budget_cooldown boolean DEFAULT false NOT NULL,
    spend double precision DEFAULT 0.0 NOT NULL,
    expires timestamp(3) without time zone,
    models text[],
    aliases jsonb DEFAULT '{}'::jsonb NOT NULL,
    config jsonb DEFAULT '{}'::jsonb NOT NULL,
    user_id text,
    team_id text,
    permissions jsonb DEFAULT '{}'::jsonb NOT NULL,
    max_parallel_requests integer,
    metadata jsonb DEFAULT '{}'::jsonb NOT NULL,
    blocked boolean,
    tpm_limit bigint,
    rpm_limit bigint,
    max_budget double precision,
    budget_duration text,
    budget_reset_at timestamp(3) without time zone,
    allowed_cache_controls text[] DEFAULT ARRAY[]::text[],
    model_spend jsonb DEFAULT '{}'::jsonb NOT NULL,
    model_max_budget jsonb DEFAULT '{}'::jsonb NOT NULL,
    budget_id text,
    organization_id text,
    created_at timestamp(3) without time zone DEFAULT CURRENT_TIMESTAMP,
    created_by text,
    updated_at timestamp(3) without time zone DEFAULT CURRENT_TIMESTAMP,
    updated_by text,
    allowed_routes text[] DEFAULT ARRAY[]::text[],
    object_permission_id text,
    auto_rotate boolean DEFAULT false,
    key_rotation_at timestamp(3) without time zone,
    last_rotation_at timestamp(3) without time zone,
    rotation_count integer DEFAULT 0,
    rotation_interval text,
    project_id text,
    router_settings jsonb DEFAULT '{}'::jsonb,
    policies text[] DEFAULT ARRAY[]::text[],
    access_group_ids text[] DEFAULT ARRAY[]::text[],
    last_active timestamp(3) without time zone,
    agent_id text,
    budget_limits jsonb,
    budget_fallbacks jsonb DEFAULT '{}'::jsonb NOT NULL,
    key_type text,
    settings_updated_at timestamp(3) without time zone
);

CREATE TABLE public."LiteLLM_UserTable" (
    user_id text NOT NULL,
    user_alias text,
    team_id text,
    sso_user_id text,
    organization_id text,
    password text,
    teams text[] DEFAULT ARRAY[]::text[],
    user_role text,
    max_budget double precision,
    spend double precision DEFAULT 0.0 NOT NULL,
    user_email text,
    models text[],
    metadata jsonb DEFAULT '{}'::jsonb NOT NULL,
    max_parallel_requests integer,
    tpm_limit bigint,
    rpm_limit bigint,
    budget_duration text,
    budget_reset_at timestamp(3) without time zone,
    allowed_cache_controls text[] DEFAULT ARRAY[]::text[],
    model_spend jsonb DEFAULT '{}'::jsonb NOT NULL,
    model_max_budget jsonb DEFAULT '{}'::jsonb NOT NULL,
    created_at timestamp(3) without time zone DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamp(3) without time zone DEFAULT CURRENT_TIMESTAMP,
    object_permission_id text,
    policies text[] DEFAULT ARRAY[]::text[]
);

ALTER TABLE ONLY public."LiteLLM_SpendLogs"
    ADD CONSTRAINT "LiteLLM_SpendLogs_pkey" PRIMARY KEY (request_id);

ALTER TABLE ONLY public."LiteLLM_UserTable"
    ADD CONSTRAINT "LiteLLM_UserTable_pkey" PRIMARY KEY (user_id);

ALTER TABLE ONLY public."LiteLLM_VerificationToken"
    ADD CONSTRAINT "LiteLLM_VerificationToken_pkey" PRIMARY KEY (token);

CREATE INDEX "LiteLLM_SpendLogs_end_user_idx" ON public."LiteLLM_SpendLogs" USING btree (end_user);

CREATE INDEX "LiteLLM_SpendLogs_session_id_idx" ON public."LiteLLM_SpendLogs" USING btree (session_id);

CREATE INDEX "LiteLLM_SpendLogs_startTime_idx" ON public."LiteLLM_SpendLogs" USING btree ("startTime");

CREATE INDEX "LiteLLM_SpendLogs_startTime_request_id_idx" ON public."LiteLLM_SpendLogs" USING btree ("startTime", request_id);

CREATE UNIQUE INDEX "LiteLLM_UserTable_sso_user_id_key" ON public."LiteLLM_UserTable" USING btree (sso_user_id);

CREATE INDEX "LiteLLM_UserTable_user_email_lower_idx" ON public."LiteLLM_UserTable" USING btree (lower(user_email));

CREATE INDEX "LiteLLM_VerificationToken_budget_reset_at_expires_idx" ON public."LiteLLM_VerificationToken" USING btree (budget_reset_at, expires);

CREATE INDEX "LiteLLM_VerificationToken_team_id_idx" ON public."LiteLLM_VerificationToken" USING btree (team_id);

CREATE INDEX "LiteLLM_VerificationToken_user_id_team_id_idx" ON public."LiteLLM_VerificationToken" USING btree (user_id, team_id);

