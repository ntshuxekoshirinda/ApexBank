using Microsoft.EntityFrameworkCore;

public class TransferService {
    private readonly AppDbContext _context;
    private readonly IBackgroundTaskQueue _taskQueue;

    public TransferService(AppDbContext context, IBackgroundTaskQueue taskQueue) {
        _context = context;
        _taskQueue = taskQueue;
    }

    public async Task<TransferResult> ExecuteTransferAsync(TransferRequestDto dto, Guid authenticatedUserId) {
        // 1. IDEMPOTENCY CHECK: If this request key was already processed, return original result
        var existingTransaction = await _context.Transactions
            .FirstOrDefaultAsync(t => t.IdempotencyKey == dto.IdempotencyKey);

        if (existingTransaction != null) {
            return new TransferResult(
                existingTransaction.Id,
                existingTransaction.Status.ToString(),
                "Duplicate request detected. Returning existing transaction result (Idempotency enforced)."
            );
        }

        try {
            // Fetch source and destination accounts
            var sourceAccount = await _context.Accounts
                .FirstOrDefaultAsync(a => a.Id == dto.SourceAccountId);
            
            var destAccount = await _context.Accounts
                .FirstOrDefaultAsync(a => a.Id == dto.DestinationAccountId);

            if (sourceAccount == null || destAccount == null) {
                return new TransferResult(Guid.Empty, "Failed", "Source or Destination account could not be found.");
            }

            // Security check: Ensure the authenticated user owns the source account
            if (sourceAccount.UserId != authenticatedUserId) {
                return new TransferResult(Guid.Empty, "Unauthorized", "You do not have permission to debit from this account.");
            }

            // Validate currency matching
            if (sourceAccount.CurrencyCode != dto.CurrencyCode.ToUpper() || 
                destAccount.CurrencyCode != dto.CurrencyCode.ToUpper()) {
                return new TransferResult(Guid.Empty, "Failed", "Currency mismatch between accounts and transfer payload.");
            }

            // Check sufficient balance
            if (sourceAccount.Balance < dto.Amount) {
                return new TransferResult(Guid.Empty, "Failed", "Insufficient funds in source account.");
            }

            // 2. EXECUTE BALANCE MUTATIONS & INCREMENT CONCURRENCY VERSION
            sourceAccount.Balance -= dto.Amount;
            sourceAccount.Version++;

            destAccount.Balance += dto.Amount;
            destAccount.Version++;

            // 3. CREATE AUDIT LEDGER ENTRY
            var ledgerEntry = new Transaction {
                SourceAccountId = sourceAccount.Id,
                DestinationAccountId = destAccount.Id,
                Amount = dto.Amount,
                CurrencyCode = dto.CurrencyCode.ToUpper(),
                Type = TransactionType.Transfer,
                Status = TransactionStatus.Completed,
                IdempotencyKey = dto.IdempotencyKey,
                Description = dto.Description,
                CreatedAt = DateTime.UtcNow
            };

            _context.Transactions.Add(ledgerEntry);

            // 4. SAVE CHANGES (Atomic via EF Core SaveChanges)
            await _context.SaveChangesAsync();

            // 5. ASYNCHRONOUS PIPELINE: Push audit message to background queue non-blockingly
            var auditMessage = new AuditLogMessage(
                ledgerEntry.Id,
                sourceAccount.Id,
                destAccount.Id,
                dto.Amount,
                dto.CurrencyCode.ToUpper(),
                dto.Description,
                DateTime.UtcNow
            );

            await _taskQueue.QueueBackgroundWorkItemAsync(auditMessage);

            return new TransferResult(ledgerEntry.Id, "Completed", "Transfer completed successfully.");
        }
        catch (DbUpdateConcurrencyException) {
            return new TransferResult(
                Guid.Empty, 
                "Conflict", 
                "A concurrent transaction modified one of these accounts simultaneously. Please retry your request."
            );
        }
        catch (Exception ex) {
            return new TransferResult(Guid.Empty, "Error", $"An unexpected system error occurred during transfer: {ex.Message}");
        }
    }
}

public record TransferResult(Guid TransactionId, string Status, string Message);