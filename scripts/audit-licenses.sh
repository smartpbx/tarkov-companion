#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly TASK_DEFAULT_MAPPING="${TASK_PROJECT_ROOT}/licenses/dependency-license-map.json"
readonly TASK_LOCKED_INVENTORY="${TASK_PROJECT_ROOT}/docs/THIRD_PARTY_INVENTORY.json"
readonly TASK_DEFAULT_OUTPUT="${TASK_PROJECT_ROOT}/artifacts/licenses/THIRD_PARTY_INVENTORY.json"
readonly TASK_DEFAULT_NOTICES="${TASK_PROJECT_ROOT}/docs/THIRD_PARTY_NOTICES.md"

TASK_MAPPING="${TASK_DEFAULT_MAPPING}"
TASK_GRAPH=""
TASK_OUTPUT="${TASK_DEFAULT_OUTPUT}"
TASK_NOTICES="${TASK_DEFAULT_NOTICES}"
TASK_WRITE_LOCK=false

usage() {
    cat <<'EOF'
Usage: scripts/audit-licenses.sh [options]

Generate and validate the deterministic dependency/license inventory from restored
project.assets.json files. This command never restores packages or contacts the network.

Options:
  --mapping FILE  Use an alternate exact package/license mapping.
  --graph FILE    Use a normalized graph fixture instead of restored project assets.
  --notices FILE  Use an alternate third-party notices document.
  --output FILE   Also write the validated inventory to FILE.
  --write          Replace docs/THIRD_PARTY_INVENTORY.json with generated output.
  --help           Show this help.
EOF
}

while (($# > 0)); do
    case "$1" in
        --mapping)
            TASK_MAPPING="$2"
            shift 2
            ;;
        --graph)
            TASK_GRAPH="$2"
            shift 2
            ;;
        --notices)
            TASK_NOTICES="$2"
            shift 2
            ;;
        --output)
            TASK_OUTPUT="$2"
            shift 2
            ;;
        --write)
            TASK_WRITE_LOCK=true
            shift
            ;;
        --help)
            usage
            exit 0
            ;;
        *)
            printf 'License audit failed: unknown argument %s\n' "$1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

for command_name in jq grep; do
    if ! command -v "${command_name}" >/dev/null 2>&1; then
        printf 'License audit failed: required command is unavailable: %s\n' "${command_name}" >&2
        exit 1
    fi
done

for required_file in "${TASK_MAPPING}" "${TASK_NOTICES}"; do
    if [[ ! -f "${required_file}" ]]; then
        printf 'License audit failed: required file is missing: %s\n' "${required_file}" >&2
        exit 1
    fi
done

readonly TASK_TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/tarkov-license-audit.XXXXXX")"
trap 'rm -rf -- "${TASK_TEMP_DIR}"' EXIT
readonly TASK_RESOLVED_GRAPH="${TASK_TEMP_DIR}/resolved-graph.json"
readonly TASK_GENERATED_INVENTORY="${TASK_TEMP_DIR}/THIRD_PARTY_INVENTORY.json"

collect_project_fragment() {
    local kind="$1"
    local project="$2"
    local relative_project="${project#${TASK_PROJECT_ROOT}/}"
    local assets="$(dirname "${project}")/obj/project.assets.json"
    local output="$3"

    if [[ ! -f "${assets}" ]]; then
        printf 'License audit failed: restore output is missing for %s (%s)\n' \
            "${relative_project}" "${assets}" >&2
        exit 1
    fi

    jq \
        --arg kind "${kind}" \
        --arg project "${relative_project}" \
        '
        ([.project.frameworks[]? | (.dependencies // {}) | keys[]] | unique) as $direct
        | {
            kind: $kind,
            project: $project,
            packages: [
                .libraries
                | to_entries[]
                | select(.value.type == "package")
                | (.key | split("/")) as $keyParts
                | ($keyParts[0:-1] | join("/")) as $id
                | {
                    id: $id,
                    version: $keyParts[-1],
                    contentHash: .value.sha512,
                    direct: (($direct | index($id)) != null),
                    project: $project
                  }
            ]
          }
        ' "${assets}" > "${output}"
}

if [[ -n "${TASK_GRAPH}" ]]; then
    if [[ ! -f "${TASK_GRAPH}" ]]; then
        printf 'License audit failed: graph fixture is missing: %s\n' "${TASK_GRAPH}" >&2
        exit 1
    fi
    cp "${TASK_GRAPH}" "${TASK_RESOLVED_GRAPH}"
else
    fragment_index=0
    fragment_files=()
    for project in "${TASK_PROJECT_ROOT}"/src/*/*.csproj; do
        fragment="${TASK_TEMP_DIR}/fragment-${fragment_index}.json"
        collect_project_fragment source "${project}" "${fragment}"
        fragment_files+=("${fragment}")
        fragment_index=$((fragment_index + 1))
    done
    for project in "${TASK_PROJECT_ROOT}"/tests/*/*.csproj; do
        fragment="${TASK_TEMP_DIR}/fragment-${fragment_index}.json"
        collect_project_fragment test "${project}" "${fragment}"
        fragment_files+=("${fragment}")
        fragment_index=$((fragment_index + 1))
    done

    jq -s '
        def packageKey($package):
            (($package.id | ascii_downcase) + "@" + $package.version);
        def aggregate:
            sort_by((.id | ascii_downcase), .version)
            | group_by(packageKey(.))
            | map(
                . as $instances
                | ([.[].contentHash] | unique) as $hashes
                | if ($hashes | length) != 1 or $hashes[0] == null then
                    error("one package/version resolved with missing or conflicting content hashes")
                  else
                    {
                      id: .[0].id,
                      version: .[0].version,
                      contentHash: $hashes[0],
                      relationship: (if any(.[]; .direct) then "direct" else "transitive" end),
                      projects: ([.[].project] | unique | sort)
                    }
                  end
              );

        (map(select(.kind == "source"))) as $sourceFragments
        | (map(select(.kind == "test"))) as $testFragments
        | ($sourceFragments | map(.packages[]) | aggregate) as $sourcePackages
        | ($testFragments | map(.packages[]) | aggregate) as $allTestPackages
        | ($sourcePackages | map(packageKey(.))) as $sourceKeys
        | {
            schemaVersion: 1,
            sourceProjects: ($sourceFragments | map(.project) | unique | sort),
            testProjects: ($testFragments | map(.project) | unique | sort),
            sourcePackages: $sourcePackages,
            testPackages: (
                $allTestPackages
                | map(. as $package | select(($sourceKeys | index(packageKey($package))) == null))
              )
          }
        ' "${fragment_files[@]}" > "${TASK_RESOLVED_GRAPH}"
fi

if ! jq -e '
    .schemaVersion == 1
    and (.sourceProjects | type == "array")
    and (.testProjects | type == "array")
    and (.sourcePackages | type == "array")
    and (.testPackages | type == "array")
    and all(.sourcePackages[], .testPackages[];
        (.id | type == "string" and length > 0)
        and (.version | type == "string" and length > 0)
        and (.contentHash | type == "string" and length > 0)
        and (.relationship == "direct" or .relationship == "transitive")
        and (.projects | type == "array" and length > 0))
    ' "${TASK_RESOLVED_GRAPH}" >/dev/null; then
    printf 'License audit failed: resolved graph has an invalid schema\n' >&2
    exit 1
fi

if ! jq -e '
    .schemaVersion == 1
    and .target == "win-x64"
    and (.packageGroups | type == "array")
    and (.bundledComponents | type == "array")
    and all(.packageGroups[];
        (.name | type == "string" and length > 0)
        and (.ids | type == "array" and length > 0)
        and (.scope == "runtime" or .scope == "build" or .scope == "platform" or .scope == "test")
        and (.ships | type == "boolean")
        and (.license | type == "string" and length > 0)
        and (.copyrightNotice | type == "string" and length > 0)
        and (.purpose | type == "string" and length > 0)
        and (.authoritativeSource | type == "string" and startswith("https://"))
        and (.licenseFiles | type == "array")
        and (.noticeKey | type == "string" and length > 0)
        and (. as $group
            | [$group.ids[] as $id | ((($group.versionOverrides // {})[$id]) // $group.version)]
            | all(.[]; type == "string" and length > 0)))
    and all(.bundledComponents[];
        (.id | type == "string" and length > 0)
        and (.name | type == "string" and length > 0)
        and (.version | type == "string" and length > 0)
        and (.ships | type == "boolean")
        and (.license | type == "string" and length > 0)
        and (.copyrightNotice | type == "string" and length > 0)
        and (.purpose | type == "string" and length > 0)
        and (.authoritativeSource | type == "string" and startswith("https://"))
        and (.licenseFiles | type == "array")
        and (.noticeKey | type == "string" and length > 0))
    ' "${TASK_MAPPING}" >/dev/null; then
    printf 'License audit failed: license mapping has an invalid schema\n' >&2
    exit 1
fi

readonly TASK_MAPPING_ISSUES="${TASK_TEMP_DIR}/mapping-issues.json"
jq -n \
    --slurpfile graph "${TASK_RESOLVED_GRAPH}" \
    --slurpfile mapping "${TASK_MAPPING}" '
    def packageKey($package):
        (($package.id | ascii_downcase) + "@" + $package.version);
    def expandGroups:
        [
          .packageGroups[] as $group
          | $group.ids[] as $id
          | $group
            + {
                id: $id,
                version: (((($group.versionOverrides // {})[$id]) // $group.version)),
                component: $group.name
              }
          | del(.ids, .versionOverrides, .name)
        ];

    ($mapping[0] | expandGroups) as $mapped
    | (($graph[0].sourcePackages + $graph[0].testPackages) | sort_by(packageKey(.))) as $actual
    | ($actual | map(packageKey(.))) as $actualKeys
    | ($mapped | map(packageKey(.))) as $mappedKeys
    | ($graph[0].sourcePackages | map(packageKey(.))) as $sourceKeys
    | ($graph[0].testPackages | map(packageKey(.))) as $testKeys
    | {
        missing: ($actual | map(. as $package | select(($mappedKeys | index(packageKey($package))) == null))),
        unexpected: ($mapped | map(. as $package | select(($actualKeys | index(packageKey($package))) == null))),
        duplicate: (
            $mapped
            | sort_by(packageKey(.))
            | group_by(packageKey(.))
            | map(select(length != 1) | .[0])
          ),
        wrongSourceScope: (
            $mapped
            | map(. as $package | select(($sourceKeys | index(packageKey($package))) != null and .scope == "test"))
          ),
        wrongTestScope: (
            $mapped
            | map(. as $package | select(($testKeys | index(packageKey($package))) != null and .scope != "test"))
          )
      }
    ' > "${TASK_MAPPING_ISSUES}"

report_package_issues() {
    local issue_name="$1"
    local message="$2"
    local count
    count="$(jq --arg issue "${issue_name}" '.[$issue] | length' "${TASK_MAPPING_ISSUES}")"
    if [[ "${count}" != "0" ]]; then
        while IFS= read -r package; do
            printf 'License audit failed: %s: %s\n' "${message}" "${package}" >&2
        done < <(jq -r --arg issue "${issue_name}" '.[$issue][] | (.id + "/" + .version)' "${TASK_MAPPING_ISSUES}")
        return 1
    fi
}

mapping_failed=false
report_package_issues missing 'missing license mapping' || mapping_failed=true
report_package_issues unexpected 'stale license mapping' || mapping_failed=true
report_package_issues duplicate 'duplicate license mapping' || mapping_failed=true
report_package_issues wrongSourceScope 'source package incorrectly marked test-only' || mapping_failed=true
report_package_issues wrongTestScope 'test-only package has a non-test scope' || mapping_failed=true
if [[ "${mapping_failed}" == true ]]; then
    exit 1
fi

while IFS=$'\t' read -r component notice_key license_files_json; do
    if [[ "$(jq 'length' <<<"${license_files_json}")" == "0" ]]; then
        printf 'License audit failed: shipped component has no license file mapping: %s\n' "${component}" >&2
        exit 1
    fi
    while IFS= read -r license_file; do
        if [[ "${license_file}" != LICENSES/* ]]; then
            printf 'License audit failed: license path is outside LICENSES/: %s\n' "${license_file}" >&2
            exit 1
        fi
        if [[ ! -s "${TASK_PROJECT_ROOT}/${license_file}" ]]; then
            printf 'License audit failed: mapped license file is missing or empty: %s\n' "${license_file}" >&2
            exit 1
        fi
    done < <(jq -r '.[]' <<<"${license_files_json}")
    if ! grep -Fq -- "<!-- notice:${notice_key} -->" "${TASK_NOTICES}"; then
        printf 'License audit failed: notice mapping is absent for %s (notice:%s)\n' \
            "${component}" "${notice_key}" >&2
        exit 1
    fi
done < <(
    jq -r '
        ([.packageGroups[], .bundledComponents[]]
        | map(select(.ships == true))
        | .[]
        | [(.name // .id), .noticeKey, (.licenseFiles | @json)]
        | @tsv)
        ' "${TASK_MAPPING}"
)

jq -n \
    --slurpfile graph "${TASK_RESOLVED_GRAPH}" \
    --slurpfile mapping "${TASK_MAPPING}" '
    def packageKey($package):
        (($package.id | ascii_downcase) + "@" + $package.version);
    def expandGroups:
        [
          .packageGroups[] as $group
          | $group.ids[] as $id
          | $group
            + {
                id: $id,
                version: (((($group.versionOverrides // {})[$id]) // $group.version)),
                component: $group.name
              }
          | del(.ids, .versionOverrides, .name)
        ];
    def publicMetadata:
        {
          component,
          scope,
          ships,
          license,
          copyrightNotice,
          purpose,
          authoritativeSource,
          licenseFiles,
          noticeKey
        };

    ($mapping[0] | expandGroups) as $mapped
    | (reduce $mapped[] as $entry ({}; .[packageKey($entry)] = $entry)) as $mappingByKey
    | ($graph[0].sourcePackages
        | map(. as $package | . + ($mappingByKey[packageKey($package)] | publicMetadata))
        | sort_by((.id | ascii_downcase), .version)) as $sourcePackages
    | ($graph[0].testPackages
        | map(. as $package | . + ($mappingByKey[packageKey($package)] | publicMetadata))
        | sort_by((.id | ascii_downcase), .version)) as $testPackages
    | {
        schemaVersion: 1,
        targetFramework: "net10.0",
        runtimeIdentifier: $mapping[0].target,
        sourceProjects: $graph[0].sourceProjects,
        testProjects: $graph[0].testProjects,
        packageGraph: {
          runtime: ($sourcePackages | map(select(.scope == "runtime"))),
          buildOnly: ($sourcePackages | map(select(.scope == "build"))),
          resolvedOtherPlatforms: ($sourcePackages | map(select(.scope == "platform"))),
          testOnly: $testPackages
        },
        bundledComponents: ($mapping[0].bundledComponents | sort_by(.id)),
        summary: {
          runtimePackages: ($sourcePackages | map(select(.scope == "runtime")) | length),
          buildOnlyPackages: ($sourcePackages | map(select(.scope == "build")) | length),
          resolvedOtherPlatformPackages: ($sourcePackages | map(select(.scope == "platform")) | length),
          testOnlyPackages: ($testPackages | length),
          shippedBundledComponents: ($mapping[0].bundledComponents | map(select(.ships == true)) | length)
        }
      }
    ' > "${TASK_GENERATED_INVENTORY}"

if [[ "${TASK_WRITE_LOCK}" == true ]]; then
    mkdir -p "$(dirname "${TASK_LOCKED_INVENTORY}")"
    cp "${TASK_GENERATED_INVENTORY}" "${TASK_LOCKED_INVENTORY}"
elif [[ ! -f "${TASK_LOCKED_INVENTORY}" ]] || ! cmp -s "${TASK_GENERATED_INVENTORY}" "${TASK_LOCKED_INVENTORY}"; then
    printf 'License audit failed: locked inventory is missing or stale; run scripts/audit-licenses.sh --write after reviewing the graph\n' >&2
    exit 1
fi

mkdir -p "$(dirname "${TASK_OUTPUT}")"
cp "${TASK_GENERATED_INVENTORY}" "${TASK_OUTPUT}"

jq -r '
    "License audit passed: runtime=\(.summary.runtimePackages), build-only=\(.summary.buildOnlyPackages), other-platform=\(.summary.resolvedOtherPlatformPackages), test-only=\(.summary.testOnlyPackages), bundled=\(.summary.shippedBundledComponents); inventory=" + $output
    ' --arg output "${TASK_OUTPUT}" "${TASK_GENERATED_INVENTORY}"
