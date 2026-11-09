using Microsoft.EntityFrameworkCore;

public class TransferService {
    private readonly AppDbContext _context;

    public TransferService(AppDbContext context) {
        _context = context;
    }

    public async Task<TransferResult> ExecuteTransferAsync(TransferRequestDto dto, Guid authenticatedUserId) {
        // 1. IDEMPOTENCY CHECK: If this request key was already processed, return the original result immediately
        var existingTransaction = await _context.Transactions
            .FirstOrDefaultAsync(t => t.IdempotencyKey == dto.IdempotencyKey);

        if (existingTransaction != null) {
            return new TransferResult(
                existingTransaction.Id,
                existingTransaction.Status.ToString(),
                "Duplicate request detected. Returning existing transaction result (Idempotency enforced)."
            );
        }

        // 2. BEGIN EXPLICIT ACID TRANSACTION SCOPE
        using var dbTransaction = await _context.Database.BeginTransactionAsync();

        try {
            // Fetch source and destination accounts
            var sourceAccount = await _context.Accounts
                .FirstOrDefaultAsync(a => a.Id == dto.SourceAccountId);
            
            var destAccount = await _context.Accounts
                .FirstOrDefaultAsync(a => a.Id == dto.DestinationAccountId);

            if (sourceAccount == null || destAccount == null) {
                await dbTransaction.RollbackAsync();
                return new TransferResult(Guid.Empty, "Failed", "Source or Destination account could not be found.");
            }

            // Security check: Ensure the authenticated user owns the source account
            if (sourceAccount.UserId != authenticatedUserId) {
                await dbTransaction.RollbackAsync();
                return new TransferResult(Guid.Empty, "Unauthorized", "You do not have permission to debit from this account.");
            }

            // Validate currency matching
            if (sourceAccount.CurrencyCode != dto.CurrencyCode.ToUpper() || 
                destAccount.CurrencyCode != dto.CurrencyCode.ToUpper()) {
                await dbTransaction.RollbackAsync();
                return new TransferResult(Guid.Empty, "Failed", "Currency mismatch between accounts and transfer payload.");
            }

            // Check sufficient balance
            if (sourceAccount.Balance < dto.Amount) {
                await dbTransaction.RollbackAsync();
                return new TransferResult(Guid.Empty, "Failed", "Insufficient funds in source account.");
            }

            // 3. EXECUTE BALANCE MUTATIONS
            sourceAccount.Balance -= dto.Amount;
            destAccount.Balance += dto.Amount;

            // 4. CREATE AUDIT LEDGER ENTRY
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

            // 5. SAVE CHANGES & COMMIT
            // EF Core automatically evaluates the [Timestamp] RowVersion on Accounts. 
            // If another thread modified either account concurrently, a DbUpdateConcurrencyException is thrown.
            await _context.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            return new TransferResult(ledgerEntry.Id, "Completed", "Transfer completed successfully.");
        }
        catch (DbUpdateConcurrencyException) {
            // RACE CONDITION CAUGHT: Roll back the transaction safely
            await dbTransaction.RollbackAsync();
            return new TransferResult(
                Guid.Empty, 
                "Conflict", 
                "A concurrent transaction modified one of these accounts simultaneously. Please retry your request."
            );
        }
        catch (Exception ex) {
            await dbTransaction.RollbackAsync();
            return new TransferResult(Guid.Empty, "Error", $"An unexpected system error occurred during transfer: {ex.Message}");
        }
    }
}

public record TransferResult(Guid TransactionId, string Status, string Message);