# Branch Protection Rules для ветки `main`

Цель правил — ни одно изменение не попадает в `main` (и далее в релиз) без ревью, зелёного CI
и связи с задачей Jira. Это часть Change Management: каждый коммит в `main` = согласованное изменение
с цифровым следом `Jira → PR → commit → CI → образ → релиз`.

## Настройки (GitHub → Settings → Branches → Branch protection rule, pattern `main`)

| Настройка | Значение | Зачем |
|---|---|---|
| Require a pull request before merging | ✅ | прямой `push` в `main` запрещён |
| Required number of approvals | **2** | «четыре глаза» + второй взгляд на миграции |
| Dismiss stale pull request approvals when new commits are pushed | ✅ | одобрение относится к конкретному коду, а не к PR в целом |
| Require review from Code Owners | ✅ | изменения `db/migrations/` обязательно одобряет DBA (см. `.github/CODEOWNERS`) |
| Require approval of the most recent reviewable push | ✅ | автор не может «досунуть» коммит после одобрения |
| Require status checks to pass before merging | ✅ | см. список ниже |
| Require branches to be up to date before merging | ✅ | CI проверяет именно тот код, который окажется в `main` |
| Require conversation resolution before merging | ✅ | все замечания ревью закрыты |
| Require signed commits | ✅ | подтверждение авторства коммитов |
| Require linear history | ✅ | только squash / rebase merge — история читается как журнал изменений |
| Do not allow bypassing the above settings | ✅ | правила действуют и для администраторов |
| Allow force pushes | ❌ | историю `main` переписывать нельзя |
| Allow deletions | ❌ | ветку `main` удалить нельзя |

### Обязательные проверки CI/CD (Required status checks)

Определены в [`.github/workflows/homework-5-ci.yml`](../../.github/workflows/homework-5-ci.yml):

| Check | Что проверяет |
|---|---|
| `commit-lint` | заголовок PR и каждый коммит начинаются с `[CAMPUS-NNN]` — связь с Jira |
| `build-and-test` | `dotnet build` без ошибок, все xUnit-тесты (unit + tracing + sanity in-process) зелёные |
| `db-migrations` | на чистом PostgreSQL 16 последовательно применяются `00 → 01 → 02 → 03`, а также проверяется откат `01_expand_rollback.sql` |

### Правила для релизов

* Теги `v*` создаются только из `main` (Tag protection / ruleset `release-tags`), право — у роли Maintain.
* Environment `production` в GitHub Actions требует ручного одобрения (Required reviewers: тимлид, SRE)
  и разрешает деплой только из тегов `v*`.

## То же самое через API (воспроизводимо, «настройки как код»)

```bash
gh api -X PUT repos/{owner}/{repo}/branches/main/protection \
  -H "Accept: application/vnd.github+json" \
  --input homework-5/docs/branch-protection.json
gh api -X POST repos/{owner}/{repo}/branches/main/protection/required_signatures
```

Содержимое [`branch-protection.json`](branch-protection.json):

```json
{
  "required_status_checks": {
    "strict": true,
    "contexts": ["commit-lint", "build-and-test", "db-migrations"]
  },
  "enforce_admins": true,
  "required_pull_request_reviews": {
    "dismiss_stale_reviews": true,
    "require_code_owner_reviews": true,
    "required_approving_review_count": 2,
    "require_last_push_approval": true
  },
  "restrictions": null,
  "required_linear_history": true,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "required_conversation_resolution": true
}
```

> Примечание: учебный репозиторий ведёт один автор, поэтому при фактическом применении правил
> число обязательных одобрений пришлось бы уменьшить до 0 — иначе PR невозможно слить.
> Описанная конфигурация — целевая для командной разработки «Умного кампуса».
