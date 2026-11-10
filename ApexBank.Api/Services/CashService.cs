using Microsoft.EntityFrameworkCore;

public class CashService {
    private readonly AppDbContext _context;
    private readonly IBackgroundTaskQueue _taskQueue;

    public CashService(AppDbContext context, IBackgroundTaskQueue taskQueue) {
        _context = context;
        _taskQueue = taskQueue;
    }

    // --- DEPOSIT LOGIC ---
    public async Task<CashResult> ExecuteDepositAsync(DepositRequestDto dto, Guid authenticatedUserId) {
        // 1. Idempotency Check
        var existingTx = await _context.Transactions.FirstOrDefaultAsync(t => t.IdempotencyKey == dto.IdempotencyKey);
        if (existingTx != null) {
            return new CashResult(existingTx.Id, existingTx.Status.ToString(), "Duplicate request detected. Existing transaction returned.");
        }

        try {
            var account = await _context.Accounts.FirstOrDefaultAsync(a => a.Id == dto.AccountId);
            if (account == null) {
                return new CashResult(Guid.Empty, "Failed", "Account not found.");
            }

            // Security: Ensure authenticated user owns the account
            if (account.UserId != authenticatedUserId) {
                return new CashResult(Guid.Empty, "Unauthorized", "You do not own this account.");
            }

            if (account.CurrencyCode != dto.CurrencyCode.ToUpper()) {
                return new CashResult(Guid.Empty, "Failed", "Currency mismatch.");
            }

            // 2. Mutate Balance & Version
            account.Balance += dto.Amount;
            account.Version++;

            // 3. Create Audit Ledger Entry
            var ledgerEntry = new Transaction {
                DestinationAccountId = account.Id,
                Amount = dto.Amount,
                CurrencyCode = dto.CurrencyCode.ToUpper(),
                Type = TransactionType.Deposit,
                Status = TransactionStatus.Completed,
                IdempotencyKey = dto.IdempotencyKey,
                Description = string.IsNullOrEmpty(dto.Description) ? "Cash Deposit" : dto.Description,
                CreatedAt = DateTime.UtcNow
            };

            _context.Transactions.Add(ledgerEntry);
            await _context.SaveChangesAsync();

            // 4. Queue Background Audit Event
            await _taskQueue.QueueBackgroundWorkItemAsync(new AuditLogMessage(
                ledgerEntry.Id, null, account.Id, dto.Amount, dto.CurrencyCode.ToUpper(), ledgerEntry.Description, DateTime.UtcNow
            ));

            return new CashResult(ledgerEntry.Id, "Completed", "Deposit completed successfully.");
        }
        catch (DbUpdateConcurrencyException) {
            return new CashResult(Guid.Empty, "Conflict", "Concurrent modification detected. Please retry.");
        }
        catch (Exception ex) {
            return new CashResult(Guid.Empty, "Error", $"Unexpected error: {ex.Message}");
        }
    }

    // --- WITHDRAWAL LOGIC ---
    public async Task<CashResult> ExecuteWithdrawalAsync(WithdrawalRequestDto dto, Guid authenticatedUserId) {
        // 1. Idempotency Check
        var existingTx = await _context.Transactions.FirstOrDefaultAsync(t => t.IdempotencyKey == dto.IdempotencyKey);
        if (existingTx != null) {
            return new CashResult(existingTx.Id, existingTx.Status.ToString(), "Duplicate request detected. Existing transaction returned.");
        }

        try {
            var account = await _context.Accounts.FirstOrDefaultAsync(a => a.Id == dto.AccountId);
            if (account == null) {
                return new CashResult(Guid.Empty, "Failed", "Account not found.");
            }

            // Security: Ensure authenticated user owns the account
            if (account.UserId != authenticatedUserId) {
                return new CashResult(Guid.Empty, "Unauthorized", "You do not own this account.");
            }

            if (account.CurrencyCode != dto.CurrencyCode.ToUpper()) {
                return new CashResult(Guid.Empty, "Failed", "Currency mismatch.");
            }

            // Sufficient Funds Check
            if (account.Balance < dto.Amount) {
                return new CashResult(Guid.Empty, "Failed", "Insufficient funds.");
            }

            // 2. Mutate Balance & Version
            account.Balance -= dto.Amount;
            account.Version++;

            // 3. Create Audit Ledger Entry
            var ledgerEntry = new Transaction {
                SourceAccountId = account.Id,
                Amount = dto.Amount,
                CurrencyCode = dto.CurrencyCode.ToUpper(),
                Type = TransactionType.Withdrawal,
                Status = TransactionStatus.Completed,
                IdempotencyKey = dto.IdempotencyKey,
                Description = string.IsNullOrEmpty(dto.Description) ? "Cash Withdrawal" : dto.Description,
                CreatedAt = DateTime.UtcNow
            };

            _context.Transactions.Add(ledgerEntry);
            await _context.SaveChangesAsync();

            // 4. Queue Background Audit Event
            await _taskQueue.QueueBackgroundWorkItemAsync(new AuditLogMessage(
                ledgerEntry.Id, account.Id, null, dto.Amount, dto.CurrencyCode.ToUpper(), ledgerEntry.Description, DateTime.UtcNow
            ));

            return new CashResult(ledgerEntry.Id, "Completed", "Withdrawal completed successfully.");
        }
        catch (DbUpdateConcurrencyException) {
            return new CashResult(Guid.Empty, "Conflict", "Concurrent modification detected. Please retry.");
        }
        catch (Exception ex) {
            return new CashResult(Guid.Empty, "Error", $"Unexpected error: {ex.Message}");
        }
    }
}

public record CashResult(Guid TransactionId, string Status, string Message);