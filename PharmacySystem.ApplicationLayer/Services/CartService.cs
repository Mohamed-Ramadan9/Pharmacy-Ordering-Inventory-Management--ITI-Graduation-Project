﻿using AutoMapper;
using E_Commerce.DomainLayer.Interfaces;
using PharmacySystem.ApplicationLayer.DTOs.Cart;
using PharmacySystem.ApplicationLayer.DTOs.Cart.Read;
using PharmacySystem.ApplicationLayer.DTOs.Cart.Write;
using PharmacySystem.ApplicationLayer.IServiceInterfaces;
using PharmacySystem.DomainLayer.Common;
using PharmacySystem.DomainLayer.Entities;
using PharmacySystem.DomainLayer.Entities.Constants;
using PharmacySystem.DomainLayer.Interfaces;

namespace PharmacySystem.ApplicationLayer.Services
{
    public class CartService : ICartService
    {
        #region
        private readonly IUnitOfWork unitOfWork;
        private readonly IWarehouseRepository warehouseRepository;
        private readonly IMapper mapper;
        public CartService(IUnitOfWork unitOfWork ,IWarehouseRepository warehouseRepository, IMapper mapper)
        {
            this.unitOfWork = unitOfWork;
            this.warehouseRepository = warehouseRepository;
            this.mapper = mapper;
        }
        #endregion

        public async Task<CartDto?> GetCartAsync(int pharmacyId)
        {
            var cart = await unitOfWork.cartRepository.GetCartWithDetailsByPharmacyIdAsync(pharmacyId);
            if (cart == null) return null;

            //var pharmacy = await unitOfWork.PharmacyRepository.GetByIdAsync(pharmacyId);
            //decimal? minWarehousePrice = null;
            //if (pharmacy != null)
            //{
            //    var wareHouseAreas = unitOfWork.WareHouseAreaRepository.FindAsync(wa => wa.AreaId == pharmacy.AreaId);
            //    if (wareHouseAreas.Any())
            //    {
            //        minWarehousePrice = wareHouseAreas.Min(wa => wa.MinmumPrice);
            //    }
            //}
            var pharmacyAreaId = cart.Pharmacy?.AreaId;
            var now = DateTime.UtcNow;
            bool modified = false;

            foreach (var warehouse in cart.CartWarehouses.ToList())
            {
                foreach (var item in warehouse.CartItems.ToList())
                {
                    if ((now - item.CreatedAt).TotalHours >= 75)
                    {
                        warehouse.CartItems.Remove(item);
                        modified = true;
                    }
                }

                if (!warehouse.CartItems.Any())
                {
                    cart.CartWarehouses.Remove(warehouse);
                    modified = true;
                }
            }

            if (modified)  // update cart if any update exist
            {
                cart.TotalQuantity = cart.CartWarehouses.Sum(w => w.CartItems.Sum(i => i.Quantity));
                cart.TotalPrice = cart.CartWarehouses.Sum(w => w.CartItems.Sum(i => i.Price * i.Quantity));

                await unitOfWork.cartRepository.UpdateAsync(cart);
                await unitOfWork.SaveAsync();
            }

            int totalQuantity = cart.CartWarehouses.SelectMany(w => w.CartItems).Sum(i => i.Quantity);

            decimal totalPriceBeforeDiscount = cart.CartWarehouses.SelectMany(w => w.CartItems)
                .Sum(i => i.Price * i.Quantity);

            decimal totalPriceAfterDiscount = cart.CartWarehouses
                .SelectMany(w => w.CartItems)
                .Sum(i => (i.Price - (i.Price * (i.Discount / 100m))) * i.Quantity);

            if (!cart.CartWarehouses.Any())
                return null;

            return new CartDto
            {
                TotalQuantity = totalQuantity,
                TotalPriceBeforeDisscount = totalPriceBeforeDiscount,
                TotalPriceAfterDisscount = totalPriceAfterDiscount,
                Warehouses = cart.CartWarehouses.Select(w =>
                {
                    decimal? warehouseMinPrice = w.WareHouse?.WareHouseAreas?
                    .FirstOrDefault(wa => wa.AreaId == pharmacyAreaId)?.MinmumPrice;

                    return new CartWarehouseDto
                    {
                        WarehouseId = w.WareHouseId,
                        Name = w.WareHouse?.Name,
                        WarehouseUrl = w.WareHouse?.ImageUrl,
                        MinWarehousePriceInPharmacyArea = warehouseMinPrice,
                        Items = w.CartItems.Select(i => new CartItemDto
                        {
                            MedicineId = i.MedicineId,
                            ArabicMedicineName = i.ArabicMedicineName,
                            EnglishMedicineName = i.EnglishMedicineName,
                            MedicineUrl = i.MedicineUrl,
                            Quantity = i.Quantity,
                            Discount = i.Discount,
                            PriceBeforeDiscount = i.Price,
                            PriceAfterDiscount = i.Price - (i.Price * (i.Discount / 100m)),
                            TotalPriceForThisMedicineAfterDiscount = (i.Price - (i.Price * (i.Discount / 100m))) * i.Quantity
                        }).ToList()
                    };
                }).ToList()
            };
        }
        public async Task<OperationResult<bool>> AddToCartAsync(AddToCartDto request)
        {
            var cart = await unitOfWork.cartRepository.GetCartWithDetailsByPharmacyIdAsync(request.PharmacyId);

            var medicine = await unitOfWork.medicineRepository.GetByIdAsync(request.MedicineId);
            if (medicine == null)
                return OperationResult<bool>.Failure("Medicine not found.");

            var warehouseMedicine = await unitOfWork.warehouseMedicineRepository
                .GetWarehouseMedicineAsync(request.WarehouseId, request.MedicineId);
            if (warehouseMedicine == null)
                return OperationResult<bool>.Failure("This medicine is not available in the selected warehouse.");

            if (warehouseMedicine.Quantity < request.Quantity)
                return OperationResult<bool>.Failure($" Not enough stock. Available: {warehouseMedicine.Quantity}, Requested: {request.Quantity}");

            if (cart == null)
            {
                cart = new Cart { PharmacyId = request.PharmacyId };
                AddItemToCart(cart,request.WarehouseId,medicine.Id, medicine.ArabicName,medicine.Name,medicine.MedicineUrl,request.WarehouseUrl,medicine.Price,request.Quantity,warehouseMedicine.Discount);
                await unitOfWork.cartRepository.AddAsync(cart);
            }
            else
            {
                AddItemToCart(cart, request.WarehouseId, medicine.Id, medicine.ArabicName, medicine.Name, medicine.MedicineUrl, request.WarehouseUrl , medicine.Price, request.Quantity, warehouseMedicine.Discount);
                await unitOfWork.cartRepository.UpdateAsync(cart);
            }

            await unitOfWork.SaveAsync();

            return OperationResult<bool>.Success(true);
        }

        public async Task<OperationResult<bool>> PlaceOrderAsync(int pharmacyId, int? warehouseId = null)
        {
            var cart = await unitOfWork.cartRepository.GetCartWithDetailsByPharmacyIdAsync(pharmacyId);
            if (cart == null || !cart.CartWarehouses.Any())
                return OperationResult<bool>.Failure("Cart is empty or not found.");

            // Simplified warehouse selection
            var selectedWarehouses = warehouseId.HasValue
                ? cart.CartWarehouses.Where(cw => cw.WareHouseId == warehouseId).ToList()
                : cart.CartWarehouses.ToList();

            if (selectedWarehouses.Count == 0)
                return OperationResult<bool>.Failure("No matching warehouse found in the cart.");

            foreach (var cartWarehouse in selectedWarehouses)
            {
                var orderItems = new List<OrderDetail>();

                foreach (var cartItem in cartWarehouse.CartItems)
                {
                    var warehouseMedicine = await unitOfWork.warehouseMedicineRepository
                        .GetWarehouseMedicineAsync(cartWarehouse.WareHouseId, cartItem.MedicineId)
                        ?? throw new Exception($"Medicine {cartItem.MedicineId} not found in warehouse {cartWarehouse.WareHouseId}");

                    if (warehouseMedicine.Quantity < cartItem.Quantity)
                        throw new Exception($"Not enough stock for Medicine {cartItem.MedicineId}. Available: {warehouseMedicine.Quantity}");

                    warehouseMedicine.Quantity -= cartItem.Quantity;

                    decimal discountedPrice = cartItem.Price * (1 - cartItem.Discount / 100m);
                    orderItems.Add(new OrderDetail
                    {
                        MedicineId = cartItem.MedicineId,
                        Quntity = cartItem.Quantity,
                        Price = discountedPrice
                    });
                }

                // Create order
                await unitOfWork.orderRepository.AddAsync(new Order
                {
                    PharmacyId = pharmacyId,
                    WareHouseId = cartWarehouse.WareHouseId,
                    Quntity = orderItems.Sum(oi => oi.Quntity),
                    TotalPrice = orderItems.Sum(oi => oi.Quntity * oi.Price),
                    Status = OrderStatus.Ordered,
                    OrderDetails = orderItems
                });

                // Directly delete cart items and warehouse
                foreach (var cartItem in cartWarehouse.CartItems.ToList())
                {
                   await unitOfWork.cartItemRepository.DeleteAsync(cartItem);
                }
                await unitOfWork.cartWarehouseRepository.DeleteAsync(cartWarehouse);
                cart.CartWarehouses.Remove(cartWarehouse);
            }

            // Update or delete cart
            if (cart.CartWarehouses.Count == 0)
                await unitOfWork.cartRepository.DeleteAsync(cart);
            else
                await unitOfWork.cartRepository.UpdateAsync(cart);

            await unitOfWork.SaveAsync();
            return OperationResult<bool>.Success(true);
        }
        public async Task<OperationResult<bool>> UpdateCartItemQuantityAsync(int pharmacyId, int warehouseId, int medicineId, int newQuantity)
        {
            try
            {
                var cart = await unitOfWork.cartRepository.GetCartWithDetailsByPharmacyIdAsync(pharmacyId);
                if (cart == null) return OperationResult<bool>.Failure("Cart not found");

                var cartWarehouse = cart.CartWarehouses.FirstOrDefault(w => w.WareHouseId == warehouseId);
                if (cartWarehouse == null) return OperationResult<bool>.Failure("Warehouse not found");

                var item = cartWarehouse.CartItems.FirstOrDefault(i => i.MedicineId == medicineId);
                if (item == null) return OperationResult<bool>.Failure("Item not found");

                var warehouseMedicine = await unitOfWork.warehouseMedicineRepository.GetWarehouseMedicineAsync(warehouseId, medicineId);
                if (warehouseMedicine == null || newQuantity > warehouseMedicine.Quantity)
                    return OperationResult<bool>.Failure("Not enough stock");

                if (newQuantity == 0) cartWarehouse.CartItems.Remove(item);
                else item.Quantity = newQuantity;

                cartWarehouse.TotalQuantity = cartWarehouse.CartItems.Sum(i => i.Quantity);
                cartWarehouse.TotalPrice = cartWarehouse.CartItems.Sum(i => i.Quantity * i.Price);
                if (!cartWarehouse.CartItems.Any()) cart.CartWarehouses.Remove(cartWarehouse);

                cart.TotalQuantity = cart.CartWarehouses.Sum(w => w.TotalQuantity);
                cart.TotalPrice = cart.CartWarehouses.Sum(w => w.TotalPrice);

                await unitOfWork.cartRepository.UpdateAsync(cart);
                await unitOfWork.SaveAsync();
                return OperationResult<bool>.Success(true);
            }
            catch (Exception ex)
            {
                return OperationResult<bool>.Failure(ex.Message);
            }
        }

        public async Task<OperationResult<bool>> RemoveCartItemAsync(int pharmacyId, int warehouseId, int medicineId)
        {
            try
            {
                var cart = await unitOfWork.cartRepository.GetCartWithDetailsByPharmacyIdAsync(pharmacyId);
                if (cart == null) return OperationResult<bool>.Failure("Cart not found");

                var warehouse = cart.CartWarehouses.FirstOrDefault(w => w.WareHouseId == warehouseId);
                if (warehouse == null) return OperationResult<bool>.Failure("Warehouse not found");

                var item = warehouse.CartItems.FirstOrDefault(i => i.MedicineId == medicineId);
                if (item == null) return OperationResult<bool>.Failure("Item not found");

                await unitOfWork.cartItemRepository.DeleteAsync(item);

                warehouse.TotalQuantity = warehouse.CartItems.Sum(i => i.Quantity);
                warehouse.TotalPrice = warehouse.CartItems.Sum(i => i.Quantity * i.Price);
                if (!warehouse.CartItems.Any()) await unitOfWork.cartWarehousesRepository.DeleteAsync(warehouse);

                cart.TotalQuantity = cart.CartWarehouses.Sum(w => w.TotalQuantity);
                cart.TotalPrice = cart.CartWarehouses.Sum(w => w.TotalPrice);

                await unitOfWork.cartRepository.UpdateAsync(cart);
                await unitOfWork.SaveAsync();
                return OperationResult<bool>.Success(true);
            }
            catch (Exception ex)
            {
                return OperationResult<bool>.Failure(ex.Message);
            }
        }

        public async Task<OperationResult<bool>> RemoveWarehouseFromCartAsync(int pharmacyId, int warehouseId)
        {
            try
            {
                var cart = await unitOfWork.cartRepository.GetCartWithDetailsByPharmacyIdAsync(pharmacyId);
                if (cart == null) return OperationResult<bool>.Failure("Cart not found");

                var warehouse = cart.CartWarehouses.FirstOrDefault(w => w.WareHouseId == warehouseId);
                if (warehouse == null) return OperationResult<bool>.Failure("Warehouse not found");

                cart.CartWarehouses.Remove(warehouse);
                await unitOfWork.cartWarehousesRepository.DeleteAsync(warehouse);

                cart.TotalQuantity = cart.CartWarehouses.Sum(w => w.TotalQuantity);
                cart.TotalPrice = cart.CartWarehouses.Sum(w => w.TotalPrice);

                await unitOfWork.cartRepository.UpdateAsync(cart);
                await unitOfWork.SaveAsync();

                return OperationResult<bool>.Success(true);
            }
            catch (Exception ex)
            {
                return OperationResult<bool>.Failure(ex.Message);
            }
        }

        private OperationResult<bool> AddItemToCart(Cart cart, int warehouseId, int medicineId, string arabicName, string englishName,
                    string medicineUrl, string warehouseUrl, decimal price, int quantity, decimal discountPercentage)
                        {
                            if (price < 0 || discountPercentage < 0 || discountPercentage > 100)
                                return OperationResult<bool>.Failure("Price or discount value is invalid");

                            var warehouse = cart.CartWarehouses.FirstOrDefault(w => w.WareHouseId == warehouseId);
                            if (warehouse == null)
                            {
                                warehouse = new CartWarehouse
                                {
                                    WareHouseId = warehouseId,
                                    CartItems = new List<CartItem>()
                                };
                                cart.CartWarehouses.Add(warehouse);
                            }

                            var item = warehouse.CartItems.FirstOrDefault(i => i.MedicineId == medicineId);
                            if (item != null)
                            {
                                // in case item increase i will increase his quantity in database not his price or discount
                                item.Quantity += quantity;
                            }
                            else
                            {
                                // store medicine for the first time 
                                warehouse.CartItems.Add(new CartItem
                                {
                                    MedicineId = medicineId,
                                    ArabicMedicineName = arabicName,
                                    EnglishMedicineName = englishName,
                                    MedicineUrl = medicineUrl,
                                    WarehouseUrl = warehouseUrl,
                                    Quantity = quantity,
                                    Price = price,   // store the original price in database
                                    Discount = discountPercentage,
                                    CreatedAt = DateTime.UtcNow
                                });
                            }

                            // Update Everything After adding like  (TotalQuantity ,TotalPrice) for warehouse
                            // and TotalQuantity , TotalPrice for cart
                            warehouse.TotalQuantity = warehouse.CartItems.Sum(i => i.Quantity);
                            warehouse.TotalPrice = warehouse.CartItems.Sum(i => i.Quantity * i.Price);
                            cart.TotalQuantity = cart.CartWarehouses.Sum(w => w.CartItems.Sum(i => i.Quantity));
                            cart.TotalPrice = cart.CartWarehouses.Sum(w => w.CartItems.Sum(i => i.Quantity * i.Price));

                            return OperationResult<bool>.Success(true);
                        }

    }

}