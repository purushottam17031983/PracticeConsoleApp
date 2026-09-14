# Builds PracticeConsoleApp.SqsBulkPushFunction as an AWS Lambda *container
# image* (as opposed to the zip package aws-lambda-tools-defaults.json is set
# up for). Build from the repository root so both project directories are in
# the build context:
#   docker build -f PracticeConsoleApp.SqsBulkPushFunction/Dockerfile -t sqs-bulk-push-function .

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY PracticeConsoleApp.SqsBulkPushFunction/PracticeConsoleApp.SqsBulkPushFunction.csproj PracticeConsoleApp.SqsBulkPushFunction/
RUN dotnet restore PracticeConsoleApp.SqsBulkPushFunction/PracticeConsoleApp.SqsBulkPushFunction.csproj

# The function project has no ProjectReference to PracticeConsoleApp (a lower
# TFM can't reference a higher one) - it links its .cs files directly, so
# they need to be present at the same relative path here too.
COPY PracticeConsoleApp/*.cs PracticeConsoleApp/
COPY PracticeConsoleApp.SqsBulkPushFunction/ PracticeConsoleApp.SqsBulkPushFunction/

RUN dotnet publish PracticeConsoleApp.SqsBulkPushFunction/PracticeConsoleApp.SqsBulkPushFunction.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# ---- Runtime stage: AWS's base image implements the Lambda Runtime API ----
FROM public.ecr.aws/lambda/dotnet:8
COPY --from=build /app/publish ${LAMBDA_TASK_ROOT}

# Must match aws-lambda-tools-defaults.json's function-handler.
CMD ["PracticeConsoleApp.SqsBulkPushFunction::PracticeConsoleApp.SqsBulkPushFunction::FunctionHandler"]
