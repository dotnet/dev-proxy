// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.OpenTelemetry;

public static class SemanticConvention
{
    // GenAI General
    public const string GEN_AI_ENDPOINT = "gen_ai.endpoint";
    public const string GEN_AI_SYSTEM = "gen_ai.system";
    public const string GEN_AI_ENVIRONMENT = "gen_ai.environment";
    public const string GEN_AI_APPLICATION_NAME = "gen_ai.application_name";
    public const string GEN_AI_OPERATION = "gen_ai.type";
    public const string GEN_AI_OPERATION_NAME = "gen_ai.operation.name";
    public const string GEN_AI_HUB_OWNER = "gen_ai.hub.owner";
    public const string GEN_AI_HUB_REPO = "gen_ai.hub.repo";
    public const string GEN_AI_RETRIEVAL_SOURCE = "gen_ai.retrieval.source";
    public const string GEN_AI_REQUESTS = "gen_ai.total.requests";

    // GenAI Request
    public const string GEN_AI_REQUEST_MODEL = "gen_ai.request.model";
    public const string GEN_AI_REQUEST_TEMPERATURE = "gen_ai.request.temperature";
    public const string GEN_AI_REQUEST_TOP_P = "gen_ai.request.top_p";
    public const string GEN_AI_REQUEST_TOP_K = "gen_ai.request.top_k";
    public const string GEN_AI_REQUEST_MAX_TOKENS = "gen_ai.request.max_tokens";
    public const string GEN_AI_REQUEST_IS_STREAM = "gen_ai.request.is_stream";
    public const string GEN_AI_REQUEST_USER = "gen_ai.request.user";
    public const string GEN_AI_REQUEST_SEED = "gen_ai.request.seed";
    public const string GEN_AI_REQUEST_FREQUENCY_PENALTY = "gen_ai.request.frequency_penalty";
    public const string GEN_AI_REQUEST_PRESENCE_PENALTY = "gen_ai.request.presence_penalty";
    public const string GEN_AI_REQUEST_ENCODING_FORMATS = "gen_ai.request.embedding_format";
    public const string GEN_AI_REQUEST_EMBEDDING_DIMENSION = "gen_ai.request.embedding_dimension";
    public const string GEN_AI_REQUEST_TOOL_CHOICE = "gen_ai.request.tool_choice";
    public const string GEN_AI_REQUEST_AUDIO_VOICE = "gen_ai.request.audio_voice";
    public const string GEN_AI_REQUEST_AUDIO_RESPONSE_FORMAT = "gen_ai.request.audio_response_format";
    public const string GEN_AI_REQUEST_AUDIO_SPEED = "gen_ai.request.audio_speed";
    public const string GEN_AI_REQUEST_FINETUNE_STATUS = "gen_ai.request.fine_tune_status";
    public const string GEN_AI_REQUEST_FINETUNE_MODEL_SUFFIX = "gen_ai.request.fine_tune_model_suffix";
    public const string GEN_AI_REQUEST_FINETUNE_MODEL_EPOCHS = "gen_ai.request.fine_tune_n_epochs";
    public const string GEN_AI_REQUEST_FINETUNE_MODEL_LRM = "gen_ai.request.learning_rate_multiplier";
    public const string GEN_AI_REQUEST_FINETUNE_BATCH_SIZE = "gen_ai.request.fine_tune_batch_size";
    public const string GEN_AI_REQUEST_VALIDATION_FILE = "gen_ai.request.validation_file";
    public const string GEN_AI_REQUEST_TRAINING_FILE = "gen_ai.request.training_file";
    public const string GEN_AI_REQUEST_IMAGE_SIZE = "gen_ai.request.image_size";
    public const string GEN_AI_REQUEST_IMAGE_QUALITY = "gen_ai.request.image_quality";
    public const string GEN_AI_REQUEST_IMAGE_STYLE = "gen_ai.request.image_style";

    // GenAI Usage
    public const string GEN_AI_USAGE_INPUT_TOKENS = "gen_ai.usage.input_tokens";
    public const string GEN_AI_USAGE_OUTPUT_TOKENS = "gen_ai.usage.output_tokens";
    // OpenLIT
    public const string GEN_AI_USAGE_TOTAL_TOKENS = "gen_ai.usage.total_tokens";
    public const string GEN_AI_USAGE_COST = "gen_ai.usage.cost";
    public const string GEN_AI_USAGE_TOTAL_COST = "gen_ai.usage.total_cost";

    // GenAI Response
    public const string GEN_AI_RESPONSE_ID = "gen_ai.response.id";
    public const string GEN_AI_RESPONSE_MODEL = "gen_ai.response.model";
    public const string GEN_AI_RESPONSE_FINISH_REASON = "gen_ai.response.finish_reason";
    public const string GEN_AI_RESPONSE_IMAGE = "gen_ai.response.image";
    public const string GEN_AI_RESPONSE_IMAGE_SIZE = "gen_ai.request.image_size";
    public const string GEN_AI_RESPONSE_IMAGE_QUALITY = "gen_ai.request.image_quality"; 
    public const string GEN_AI_RESPONSE_IMAGE_STYLE = "gen_ai.request.image_style";

    // GenAI Content
    public const string GEN_AI_CONTENT_PROMPT = "gen_ai.content.prompt";
    public const string GEN_AI_CONTENT_COMPLETION = "gen_ai.completion";
    public const string GEN_AI_CONTENT_REVISED_PROMPT = "gen_ai.content.revised_prompt";

    // Operation Types
    public const string GEN_AI_OPERATION_TYPE_CHAT = "chat";
    public const string GEN_AI_OPERATION_TYPE_EMBEDDING = "embedding";
    public const string GEN_AI_OPERATION_TYPE_IMAGE = "image";
    public const string GEN_AI_OPERATION_TYPE_AUDIO = "audio";
    public const string GEN_AI_OPERATION_TYPE_FINETUNING = "fine_tuning";
    public const string GEN_AI_OPERATION_TYPE_VECTORDB = "vectordb";
    public const string GEN_AI_OPERATION_TYPE_FRAMEWORK = "framework";

    // Metrics
    public const string GEN_AI_METRIC_CLIENT_TOKEN_USAGE = "gen_ai.client.token.usage";
    public const string GEN_AI_TOKEN_TYPE = "gen_ai.token.type";
    public const string GEN_AI_TOKEN_TYPE_INPUT = "input";
    public const string GEN_AI_TOKEN_TYPE_OUTPUT = "output";
    public const string GEN_AI_METRIC_CLIENT_OPERATION_DURATION = "gen_ai.client.operation.duration";
}