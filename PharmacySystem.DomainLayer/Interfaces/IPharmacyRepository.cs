﻿using E_Commerce.InfrastructureLayer.Data.DBContext.Repositories;
using PharmacySystem.DomainLayer.Entities;

namespace PharmacySystem.DomainLayer.Interfaces;

public interface IPharmacyRepository : IGenericRepository<Pharmacy>
{
    Task<bool> EmailExistsAsync(string email);
    Task<Pharmacy?> FindByEmailAsync(string email);
    Task<Pharmacy?> FindByEmailWithRepresentativeAsync(string email);
    public Task<List<Pharmacy>> GetPharmaciesByRepresentativeIdAsync(int representativeId);
}
